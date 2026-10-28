using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Logging;
using Bulbul;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChillPatcher.Integration
{
    /// <summary>
    /// 自动日记：每次 AIChat 对话成功结束后，用同一套 LLM 配置把当天的聊天内容
    /// 汇总写进游戏原生日记（SaveDataManager → CalendarData → CalenderMonthlyData.DiaryText）。
    /// 不打开日历窗口，也不走 UI 点击。
    ///
    /// 设计要点：
    ///  - 只使用游戏自己的数据层读写（内存缓存 + ES3 存档保持一致），不直接改 .es3 文件；
    ///  - 对话完成事件来自 AIChat 插件的 ConversationCompleted（AIChatBridge 反射桥）；
    ///  - LLM 调用在后台线程，结果回到主线程 Tick 里落盘；
    ///  - 保留用户手写内容：只有“当前文本 == 上次 AI 写入的文本”时才整篇重写。
    /// </summary>
    internal static class AutoDiaryService
    {
        private const string Tag = "[AutoDiary]";

        private static readonly object Sync = new object();
        private static readonly object ResultSync = new object();

        private static ManualLogSource _log;
        private static bool _initialized;

        private static bool _subscribeTried;
        private static float _nextSubscribeAt;
        private static bool _subscribed;
        private static int _subscribeFailures;
        private static bool _disabledPermanently;
        private static string _disabledReason;

        private static readonly List<ChatLine> _pending = new List<ChatLine>();
        private static float _lastEnqueueAt;
        private static float _now;   // 主线程每帧刷新的 unscaledTime，避免后台线程读 Unity API

        // 生成中
        private static bool _generating;
        private static Task<LlmResult> _task;
        private static DateTime _genDate;
        private static string _genBaseSnapshot = "";   // 生成开始时的日记原文，用于避免覆盖用户中途的编辑
        private static List<ChatLine> _genLines;       // 本次生成消费掉的对话（写盘冲突时放回队列）

        private static string _lastError;
        private static string _lastWriteAt;
        private static DateTime? _lastWriteDate;

        private static HttpClient _http;
        private static DiaryState _state;
        private static string _statePath;

        // 一次性重写队列：从 sidecar 里已有的原始对话整篇重生成指定日期的日记
        private static readonly List<DateTime> _rewriteQueue = new List<DateTime>();
        private static DateTime _rewriteDate;
        private static int _rewriteBuildFails;
        private static string _genMode = "merge";   // merge = 正常续写，rewrite = 整篇重写

        internal sealed class ChatLine
        {
            public DateTime Date;
            public string User;
            public string Ai;
            public float At;      // Time.unscaledTime
            public long Epoch;    // Unix 秒，写入 sidecar 时使用
            public int Attempts;  // 因写盘冲突被退回队列的次数
            public bool Remembered; // 是否已写入 sidecar（退回队列后不重复记）
        }

        private sealed class LlmResult
        {
            public string Text;
            public string Error;
        }

        // ── sidecar 状态 ───────────────────────────────────────────
        private sealed class DiaryState
        {
            public Dictionary<string, DiaryDay> Days = new Dictionary<string, DiaryDay>();
        }

        private sealed class DiaryDay
        {
            public string AiText = "";
            public List<LineRecord> Lines = new List<LineRecord>();
            public string UpdatedAt = "";
        }

        private sealed class LineRecord
        {
            public string u;
            public string a;
            public long t;
        }

        public static bool IsEnabled
        {
            get
            {
                var cfg = PluginConfig.AutoDiaryEnabled;
                return cfg == null || cfg.Value;
            }
        }

        public static void Initialize(ManualLogSource log)
        {
            if (_initialized) return;
            _initialized = true;
            _log = log;
            try
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"),
                    "Nestopi",
                    "Chill With You",
                    "ChillPatcher");
                Directory.CreateDirectory(baseDir);
                _statePath = Path.Combine(baseDir, "auto_diary.json");
                LoadState();
                LoadRewriteRequest(baseDir);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"{Tag} state path init failed: {ex.Message}");
            }

            _log?.LogInfo($"{Tag} initialized, enabled={IsEnabled}, state={_statePath}");
        }

        // ── 主线程 Tick ───────────────────────────────────────────

        public static void Tick()
        {
            if (!_initialized) return;
            _now = UnityEngine.Time.unscaledTime;

            EnsureSubscribed();

            if (!IsEnabled)
            {
                if (_pending.Count > 0) _pending.Clear();
                return;
            }

            ConsumeCompletedTask();

            if (_generating) return;

            // 一次性重写优先于正常续写
            if (_rewriteQueue.Count > 0)
            {
                StartRewrite();
                return;
            }

            if (_pending.Count == 0) return;

            float debounce = PluginConfig.AutoDiaryDebounceSeconds != null
                ? Math.Max(0.2f, PluginConfig.AutoDiaryDebounceSeconds.Value)
                : 0.4f;
            if (_now - _lastEnqueueAt < debounce) return;
            StartGenerationForPending();
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed || _disabledPermanently) return;
            if (_now < _nextSubscribeAt) return;
            _nextSubscribeAt = _now + 3f;

            if (!AIChatBridge.IsAvailable)
            {
                if (!_subscribeTried)
                {
                    _subscribeTried = true;
                    _log?.LogInfo($"{Tag} AIChat not available yet, will retry");
                }
                return;
            }

            if (!AIChatBridge.TrySubscribeConversationCompleted(OnConversationCompleted, out var error))
            {
                _subscribeFailures++;
                _log?.LogWarning($"{Tag} subscribe failed ({_subscribeFailures}): {error}");
                if (_subscribeFailures >= 30)
                {
                    _disabledPermanently = true;
                    _disabledReason = error;
                    _log?.LogWarning($"{Tag} disabling auto diary: {error}");
                }
                return;
            }

            _subscribeFailures = 0;

            _subscribed = true;
            _log?.LogInfo($"{Tag} subscribed to ConversationCompleted");
        }

        private static void OnConversationCompleted(Dictionary<string, string> payload)
        {
            if (payload == null || !IsEnabled) return;

            string success = Get(payload, "Success");
            if (!string.IsNullOrEmpty(success) &&
                !success.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                !success.Equals("True", StringComparison.Ordinal))
            {
                return;
            }

            string isApiError = Get(payload, "IsApiError");
            if (!string.IsNullOrEmpty(isApiError) && isApiError.Equals("true", StringComparison.OrdinalIgnoreCase))
                return;

            string user = (Get(payload, "UserPrompt") ?? "").Trim();
            string ai = CleanAssistantText(Get(payload, "SubtitleText"));
            if (ai.Length == 0) ai = CleanAssistantText(Get(payload, "VoiceText"));
            if (ai.Length == 0) ai = CleanAssistantText(Get(payload, "RawResponse"));
            if (user.Length == 0 && ai.Length == 0) return;

            var line = new ChatLine
            {
                Date = DateTime.Now.Date,
                User = user,
                Ai = ai,
                At = _now,
                Epoch = NowEpoch(),
            };

            lock (Sync)
            {
                _pending.Add(line);
                _lastEnqueueAt = _now;
                if (_pending.Count > 60) _pending.RemoveAt(0);
            }

            _log?.LogInfo($"{Tag} captured conversation (user={user.Length}, ai={ai.Length})");
        }

        // ── 生成 ─────────────────────────────────────────────────

        /// <summary>
        /// 读取一次性的重写请求文件（与 sidecar 同目录的 rewrite_dates.json）。
        /// 内容形如 ["2026-10-26","2026-10-27"]；读完即删，只生效一次。
        /// </summary>
        private static void LoadRewriteRequest(string baseDir)
        {
            try
            {
                string path = Path.Combine(baseDir, "rewrite_dates.json");
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path, Encoding.UTF8);
                var dates = JsonConvert.DeserializeObject<List<string>>(json);
                if (dates != null)
                {
                    foreach (var s in dates)
                    {
                        if (DateTime.TryParse(s, out var d))
                            _rewriteQueue.Add(d.Date);
                    }
                }
                File.Delete(path);
                _log?.LogInfo($"{Tag} rewrite request loaded: {string.Join(", ", dates ?? new List<string>())}");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"{Tag} load rewrite request failed: {ex.Message}");
            }
        }

        private static void StartRewrite()
        {
            if (_rewriteQueue.Count == 0) return;

            DateTime date = _rewriteQueue[0];
            _rewriteDate = date;

            // 用 sidecar 里保存的原始对话重建，底稿置空 = 整篇重写
            List<ChatLine> lines = new List<ChatLine>();
            DiaryDay day = null;
            try { _state?.Days.TryGetValue(date.ToString("yyyy-MM-dd"), out day); } catch { }
            if (day != null)
            {
                foreach (var r in day.Lines)
                {
                    lines.Add(new ChatLine
                    {
                        Date = date,
                        User = r.u ?? "",
                        Ai = r.a ?? "",
                        Epoch = r.t,
                        Remembered = true,
                    });
                }
            }

            if (lines.Count == 0)
            {
                _log?.LogWarning($"{Tag} rewrite {date:yyyy-MM-dd} skipped: no sidecar lines");
                _rewriteQueue.RemoveAt(0);
                return;
            }

            var request = BuildRequest(date, "", lines, out var buildError);
            if (request == null)
            {
                _lastError = buildError;
                // LLM 配置可能还没就绪：留在队首下轮再试，连续失败太多次才放弃
                _rewriteBuildFails++;
                if (_rewriteBuildFails >= 30)
                {
                    _rewriteQueue.RemoveAt(0);
                    _rewriteBuildFails = 0;
                    _log?.LogWarning($"{Tag} rewrite {date:yyyy-MM-dd} aborted: {buildError}");
                }
                return;
            }

            _rewriteBuildFails = 0;
            _rewriteQueue.RemoveAt(0);

            _genDate = date;
            _genBaseSnapshot = GetGameDiaryText(date) ?? "";
            _genLines = null;          // 重写失败不回退成普通续写
            _genMode = "rewrite";
            _generating = true;
            _task = Task.Run(() => CallLlm(request));
            _log?.LogInfo($"{Tag} rewriting diary {date:yyyy-MM-dd} from {lines.Count} sidecar line(s)");
        }

        private static void StartGenerationForPending()
        {
            DateTime date;
            List<ChatLine> lines = new List<ChatLine>();
            lock (Sync)
            {
                // 以最早一条待处理对话的日期为准，跨午夜时不会把昨天的聊天写进今天的日记
                date = _pending[0].Date;
                // 一次只消费一条对话：每轮对话结束都立刻触发一次日记更新，
                // 不再等凑够若干条再一起写。
                lines.Add(_pending[0]);
                _pending.RemoveAt(0);
            }

            if (lines.Count == 0) return;

            // 先把原始对话存进 sidecar，生成失败也不会丢
            RememberLines(date, lines);

            string currentAtStart = "";
            string baseText = "";
            try
            {
                currentAtStart = GetGameDiaryText(date) ?? "";
                // 续写模式：已有日记一律作为底稿交给模型合并，避免每次都从零重写把旧内容冲掉
                bool incremental = PluginConfig.AutoDiaryIncremental == null || PluginConfig.AutoDiaryIncremental.Value;
                baseText = incremental ? currentAtStart : "";
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"{Tag} read existing diary failed: {ex.Message}");
            }

            var request = BuildRequest(date, baseText, lines, out var buildError);
            if (request == null)
            {
                _lastError = buildError;
                _log?.LogWarning($"{Tag} build request failed: {buildError}");
                return;
            }

            _genDate = date;
            _genBaseSnapshot = currentAtStart;
            _genLines = lines;
            _genMode = "merge";
            _generating = true;

            var task = Task.Run(() => CallLlm(request));
            _task = task;
            _log?.LogInfo($"{Tag} generating diary for {date:yyyy-MM-dd} from {lines.Count} line(s)");
        }

        private static void ConsumeCompletedTask()
        {
            var task = _task;
            if (!_generating || task == null) return;
            if (!task.IsCompleted) return;

            LlmResult result = null;
            string error = null;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
            }
            finally
            {
                _task = null;
                _generating = false;
            }

            // 本次消费掉的对话行只在这里处理一次，避免失败分支留下脏引用
            var usedLines = _genLines;
            _genLines = null;

            if (result == null || string.IsNullOrEmpty(result.Text))
            {
                _lastError = error ?? result?.Error ?? "empty response";
                _log?.LogWarning($"{Tag} generation failed: {_lastError}");
                RequeueLines(usedLines);
                return;
            }

            string text = NormalizeDiaryText(result.Text, GetMaxChars());
            if (text.Length == 0)
            {
                _lastError = "normalized to empty";
                _log?.LogWarning($"{Tag} generation produced empty text after normalize");
                RequeueLines(usedLines);
                return;
            }

            // 生成期间用户可能手动改了日记，或另一次更新已经落盘：
            // 不覆盖它，把这一批对话放回队列，下一轮以新内容为底稿重新合并。
            // 整篇重写模式下这是预期行为，跳过该检查。
            string currentNow = "";
            try { currentNow = GetGameDiaryText(_genDate) ?? ""; } catch { }
            if (_genMode != "rewrite" &&
                !string.Equals(currentNow.Trim(), (_genBaseSnapshot ?? "").Trim(), StringComparison.Ordinal))
            {
                _lastError = "diary changed during generation, requeued";
                _log?.LogInfo($"{Tag} diary changed while generating, requeue pending lines");
                RequeueLines(usedLines);
                return;
            }

            if (!WriteDiary(_genDate, text, out var writeError))
            {
                _lastError = writeError;
                _log?.LogWarning($"{Tag} write failed: {writeError}");
                RequeueLines(usedLines);
                return;
            }

            _lastError = null;
            _lastWriteAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _lastWriteDate = _genDate;
            int mergedFrom = (_genBaseSnapshot ?? "").Trim().Length;
            ChillPatcher.Integration.CalendarMemoryService.Invalidate();
            _log?.LogInfo($"{Tag} diary updated {_genDate:yyyy-MM-dd}, {mergedFrom} -> {text.Length} chars");
        }

        /// <summary>把本次生成用掉的对话退回队列（最多重试 3 次，避免外部异常时死循环）。</summary>
        private static void RequeueLines(List<ChatLine> lines)
        {
            if (lines == null || lines.Count == 0) return;

            lock (Sync)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (++line.Attempts > 3)
                    {
                        _log?.LogWarning($"{Tag} drop chat line after 3 retries");
                        continue;
                    }
                    _pending.Insert(0, line);
                }
                if (_pending.Count > 60) _pending.RemoveRange(0, _pending.Count - 60);
                _lastEnqueueAt = _now;
            }
        }

        // ── LLM 调用（后台线程） ──────────────────────────────────

        private static JObject BuildRequest(DateTime date, string baseText, List<ChatLine> lines, out string error)
        {
            error = null;

            string apiUrl = AIChatBridge.GetConfigValue("API_URL");
            string apiKey = AIChatBridge.GetConfigValue("API_Key");
            string model = AIChatBridge.GetConfigValue("ModelName");
            if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model))
            {
                error = "AIChat LLM config missing (API_URL / API_Key / ModelName)";
                return null;
            }

            string modelOverride = PluginConfig.AutoDiaryModelOverride?.Value;
            if (!string.IsNullOrEmpty(modelOverride)) model = modelOverride.Trim();

            string system = PluginConfig.AutoDiaryPromptOverride?.Value;
            if (string.IsNullOrEmpty(system)) system = DefaultSystemPrompt();

            // 最近 7 天的日记：作为压缩过的长期背景，避免只靠当天那 500 字硬续。
            string recent = "";
            try { recent = BuildRecentDaysContext(date); } catch (Exception ex) {
                _log?.LogWarning($"{Tag} read recent days failed: {ex.Message}");
            }

            string user = BuildUserContent(date, baseText, recent, lines);

            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = system },
                new JObject { ["role"] = "user", ["content"] = user },
            };

            var body = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["temperature"] = 0.8,
                ["max_tokens"] = 900,
                ["stream"] = false,
            };

            var req = new JObject
            {
                ["url"] = apiUrl,
                ["key"] = apiKey,
                ["body"] = body,
            };
            return req;
        }

        private static string DefaultSystemPrompt()
        {
            string lang = PluginConfig.AutoDiaryLanguage?.Value;
            if (string.IsNullOrEmpty(lang)) lang = "zh";
            lang = lang.Trim().ToLowerInvariant();
            int maxChars = GetMaxChars();

            if (lang == "ja")
            {
                return "あなたは日記アシスタントです。これは『キャラクター本人』の日記です。必ずキャラクターの一人称（「私」）で書き、" +
                       "プレイヤーは「ご主人様」（または「あなた」）と呼びます。プレイヤー視点や三人称の地の文では絶対に書かないこと。" +
                       "材料は三つ：『既存の日記』（今日すでに書いた分）、『ここ数日の日記』（圧縮された長期記憶。感情・関係・話題の連続性のためだけに使い、復唱や丸写しはしない）、『新しい会話』。" +
                       "既存の日記を土台に、新しい会話を書き足した「完全な日記」を自然な日本語で出力してください。" +
                       "条件：1) 既存の日記に書かれた出来事・順序・感情を残し、以前の段落を捨てない " +
                       "2) ここ数日の日記は、話題や約束や気持ちが実際につながるときだけ静かに反映する " +
                       "3) 新しい会話は時系列に沿って自然な位置に統合し、同じ内容を繰り返さない " +
                       "4) 事実が食い違う場合だけ、その一文を新しい内容に合わせて直す " +
                       "5) 実際にあった会話と気持ちだけを書き、出来事を創作しない " +
                       $"6) その日の出来事を残すことを最優先。長さは {maxChars} 字前後を目安の上限とし、削るくらいなら長くてよい " +
                       "7) 「会話ログ」「ユーザー」「AI」などの語や日付見出しを書かない、Markdown を使わない。" +
                       "日記本文だけを出力してください。";
            }

            if (lang == "en")
            {
                return "You are a diary assistant writing the CHARACTER'S OWN diary. Always write in the character's first person (\"I\"); " +
                       "the player is \"Master\" (or \"you\"). Never narrate from the player's point of view and never use third-person narration. " +
                       "You receive three inputs: EXISTING DIARY (what is already written for today), RECENT DAYS (compressed long-term memory - use it only to keep mood, relationship and topics continuous; never repeat or copy it), and NEW CHAT. " +
                       "Extend the existing diary with the new chat and return the complete diary in natural English. " +
                       "Rules: 1) keep the facts, order and mood already written - never drop earlier paragraphs; " +
                       "2) echo RECENT DAYS only when a topic, promise or feeling genuinely carries over; " +
                       "3) merge the new chat chronologically and do not repeat content; " +
                       "4) only when facts conflict, rewrite that single sentence; " +
                       $"5) invent nothing; 6) keeping the day's events matters more than brevity: treat about {maxChars} characters as a soft ceiling and prefer writing longer over dropping events or details; " +
                       "7) never mention 'chat log', 'user', 'AI', never add a date heading, no Markdown. " +
                       "Output only the diary body.";
            }

            return "你是日记助手，写的是『角色本人』的日记。必须始终由角色用第一人称（“我”）来写，玩家称为“主人”（或“你”），" +
                   "绝不能从玩家视角叙述，也不能写成第三人称的旁白。" +
                   "你会收到三份材料：『已有日记』（当天已经写好的部分）、『最近几天的日记』（压缩后的长期记忆，只用来延续情绪、关系与话题，不要复述或照抄）、『新增对话』。" +
                   "请把新对话续写进已有日记，输出完整的新日记。" +
                   "要求：1) 保留已有日记里的事实、事件顺序和情绪基调，绝不能因为有了新对话就丢掉之前写过的段落；" +
                   "2) 只有当话题、承诺或心情确实延续时，才自然地带入最近几天日记里的记忆，不要堆砌旧事；" +
                   "3) 新对话按时间顺序并入恰当的位置，不要重复已经写过的内容；" +
                   "4) 只有新旧内容冲突时，才改冲突的那一句；" +
                   "5) 只写真实发生的对话与情绪，不编造事件；" +
                   $"6) 优先完整记录当天发生的事；以 {maxChars} 字左右为宽松上限，宁可写长一些，也不要删事件、合并细节或省略情绪；" +
                   "7) 不要出现“对话记录”“用户”“AI”“助手”等字样，不要写日期标题，不要使用 Markdown。" +
                   "只输出日记正文。";
        }

        private static string BuildUserContent(DateTime date, string baseText, string recentDays, List<ChatLine> lines)
        {
            var sb = new StringBuilder();
            sb.Append("日期：").Append(date.ToString("yyyy-MM-dd")).Append('\n');

            if (!string.IsNullOrWhiteSpace(baseText))
            {
                int baseBudget = Math.Max(1500, GetMaxChars() * 2);
                string trimmedBase = baseText.Length > baseBudget ? baseText.Substring(0, baseBudget) : baseText;
                sb.Append("\n【已有日记（请在此基础上续写，不要丢弃其中已有内容）】\n").Append(trimmedBase).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(recentDays))
            {
                sb.Append("\n【最近几天的日记（压缩后的长期记忆：只用它保持情绪、关系与话题的连续性，不要复述、不要照抄）】\n")
                  .Append(recentDays).Append('\n');
            }

            sb.Append("\n【新增对话（并入上面的日记；主人 = 玩家，我 = 你自己）】\n");
            int start = 0;
            // 从最新往回塞，超长时丢弃最早的行
            var picked = new List<ChatLine>();
            int budget = 5000;
            for (int i = lines.Count - 1; i >= start; i--)
            {
                var l = lines[i];
                int cost = (l.User?.Length ?? 0) + (l.Ai?.Length ?? 0) + 8;
                if (picked.Count > 0 && budget - cost < 0) break;
                budget -= cost;
                picked.Insert(0, l);
            }

            foreach (var l in picked)
            {
                if (!string.IsNullOrEmpty(l.User))
                    sb.Append("主人：").Append(Clip(l.User, 400)).Append('\n');
                if (!string.IsNullOrEmpty(l.Ai))
                    sb.Append("我：").Append(Clip(l.Ai, 400)).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// 最近 N 天（不含当天）的日记，压缩成一份背景材料。
        /// 用途：日记本身上限只有几百字，单看当天没法维持长期连贯的情绪与关系，
        /// 所以每次续写时把过去一周的日记一起带上，相当于做一次上下文压缩。
        /// </summary>
        private static string BuildRecentDaysContext(DateTime date, int days = 7)
        {
            var sb = new StringBuilder();
            int perDay = 260;      // 单日上限
            int budget = 2400;     // 总量上限，防止把 500 字的日记挤掉
            int total = 0;

            for (int back = 1; back <= days; back++)
            {
                DateTime d = date.AddDays(-back);
                string text = null;
                try { text = GetGameDiaryText(d); } catch { text = null; }
                if (string.IsNullOrWhiteSpace(text)) continue;

                string body = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
                while (body.Contains("  ")) body = body.Replace("  ", " ");
                if (body.Length > perDay) body = body.Substring(0, perDay).TrimEnd() + "…";

                string entry = d.ToString("yyyy-MM-dd") + "：" + body;
                if (total + entry.Length > budget) break;
                sb.Append(entry).Append('\n');
                total += entry.Length;
            }

            return sb.ToString().TrimEnd();
        }

        private static LlmResult CallLlm(JObject request)
        {
            var result = new LlmResult();
            try
            {
                if (_http == null)
                {
                    _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                }

                string url = request.Value<string>("url");
                string key = request.Value<string>("key");
                string body = request.Value<JObject>("body")?.ToString(Formatting.None);

                using (var msg = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    msg.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                    msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    msg.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var resp = _http.SendAsync(msg).GetAwaiter().GetResult())
                    {
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!resp.IsSuccessStatusCode)
                        {
                            result.Error = $"HTTP {(int)resp.StatusCode}: {Clip(text, 300)}";
                            return result;
                        }

                        result.Text = ExtractContent(text);
                        if (string.IsNullOrEmpty(result.Text))
                            result.Error = "no content in response: " + Clip(text, 300);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.GetBaseException().Message;
            }

            return result;
        }

        private static string ExtractContent(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var obj = JObject.Parse(raw);
                var choices = obj["choices"] as JArray;
                if (choices != null && choices.Count > 0)
                {
                    var first = choices[0] as JObject;
                    if (first != null)
                    {
                        var message = first["message"] as JObject;
                        if (message != null)
                        {
                            var content = message["content"];
                            if (content != null && content.Type != JTokenType.Null)
                            {
                                if (content.Type == JTokenType.Array)
                                {
                                    var sb = new StringBuilder();
                                    foreach (var part in (JArray)content)
                                    {
                                        var t = part["text"];
                                        if (t != null) sb.Append(t.ToString());
                                    }
                                    if (sb.Length > 0) return sb.ToString();
                                }
                                else
                                {
                                    return content.ToString();
                                }
                            }
                        }

                        var textToken = first["text"];
                        if (textToken != null && textToken.Type != JTokenType.Null)
                            return textToken.ToString();
                    }
                }
            }
            catch
            {
                // 不是 JSON：直接当作纯文本
                return raw;
            }

            return null;
        }

        // ── 游戏日记读写 ─────────────────────────────────────────

        public static string GetTodayText()
        {
            return GetGameDiaryText(DateTime.Now.Date) ?? "";
        }

        private static string GetGameDiaryText(DateTime date)
        {
            var sm = SaveDataManager.Instance;
            var cal = sm?.CalendarData;
            if (cal == null) return null;
            var day = cal.GetDailyData(date);
            return day?.DiaryText;
        }

        private static bool WriteDiary(DateTime date, string text, out string error)
        {
            error = null;
            try
            {
                var sm = SaveDataManager.Instance;
                if (sm == null)
                {
                    error = "SaveDataManager.Instance is null";
                    return false;
                }

                var cal = sm.CalendarData;
                if (cal == null)
                {
                    error = "SaveDataManager.CalendarData is null";
                    return false;
                }

                var day = cal.GetDailyData(date);
                if (day == null)
                {
                    error = "GetDailyData returned null";
                    return false;
                }

                day.DiaryText = text;
                cal.SaveDateData(date);   // 写回 ES3 月度存档，并保持内存缓存一致

                SaveDiaryState(date, text);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>手动写入今天的日记（JSApi 使用），不触发 LLM。</summary>
        public static bool SetTodayText(string text, out string error)
        {
            if (!WriteDiary(DateTime.Now.Date, text ?? "", out error)) return false;
            _lastWriteAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _lastWriteDate = DateTime.Now.Date;
            return true;
        }

        /// <summary>立刻用已捕获的对话重算今天的日记。</summary>
        public static bool RequestRegenerate(out string error)
        {
            error = null;
            if (_generating)
            {
                error = "already generating";
                return false;
            }

            var today = DateTime.Now.Date;
            var pendingCopy = new List<ChatLine>();
            lock (Sync)
            {
                pendingCopy.AddRange(_pending);
                _pending.Clear();
            }
            if (pendingCopy.Count > 0) RememberLines(today, pendingCopy);

            var lines = LoadDayLines(today);
            if (lines.Count == 0)
            {
                error = "no conversation captured today";
                return false;
            }

            string currentAtStart = GetGameDiaryText(today) ?? "";
            bool incremental = PluginConfig.AutoDiaryIncremental == null || PluginConfig.AutoDiaryIncremental.Value;
            string baseText = incremental ? currentAtStart : "";

            var request = BuildRequest(today, baseText, lines, out var buildError);
            if (request == null)
            {
                error = buildError;
                return false;
            }

            _genDate = today;
            _genBaseSnapshot = currentAtStart;
            _genLines = lines;
            _generating = true;
            _task = Task.Run(() => CallLlm(request));
            return true;
        }

        public static string GetStatusJson()
        {
            int pending;
            lock (Sync) pending = _pending.Count;
            var today = DateTime.Now.Date;
            var day = GetDayState(today);
            var payload = new Dictionary<string, object>
            {
                ["enabled"] = IsEnabled,
                ["available"] = AIChatBridge.IsAvailable,
                ["subscribed"] = _subscribed,
                ["generating"] = _generating,
                ["pending"] = pending,
                ["today"] = today.ToString("yyyy-MM-dd"),
                ["todayAiTextLength"] = day?.AiText?.Length ?? 0,
                ["todayLines"] = day?.Lines?.Count ?? 0,
                ["lastError"] = _lastError ?? "",
                ["lastWriteAt"] = _lastWriteAt ?? "",
                ["disabled"] = _disabledPermanently,
                ["disabledReason"] = _disabledReason ?? "",
            };
            return JsApiJson(payload);
        }

        private static string JsApiJson(Dictionary<string, object> payload)
        {
            try { return ChillPatcher.JSApi.JSApiHelper.ToJson(payload); }
            catch { return "{}"; }
        }

        // ── sidecar 持久化 ───────────────────────────────────────

        private static DiaryDay GetDayState(DateTime date)
        {
            string key = date.ToString("yyyy-MM-dd");
            if (_state == null) _state = new DiaryState();
            if (!_state.Days.TryGetValue(key, out var day))
            {
                day = new DiaryDay();
                _state.Days[key] = day;
            }
            return day;
        }

        private static List<ChatLine> LoadDayLines(DateTime date)
        {
            var list = new List<ChatLine>();
            var day = GetDayState(date);
            if (day.Lines == null) return list;
            foreach (var r in day.Lines)
            {
                list.Add(new ChatLine
                {
                    Date = date,
                    User = r.u,
                    Ai = r.a,
                    Epoch = r.t,
                });
            }
            return list;
        }

        /// <summary>把已捕获的对话行追加进 sidecar（生成前调用，失败也不丢内容）。</summary>
        private static void RememberLines(DateTime date, List<ChatLine> lines)
        {
            try
            {
                var day = GetDayState(date);
                foreach (var l in lines)
                {
                    if (l.Remembered) continue;   // 被退回队列重试的对话不再重复写 sidecar
                    l.Remembered = true;
                    day.Lines.Add(new LineRecord
                    {
                        u = l.User,
                        a = l.Ai,
                        t = l.Epoch,
                    });
                }
                while (day.Lines.Count > 80) day.Lines.RemoveAt(0);
                SaveStateFile();
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"{Tag} remember lines failed: {ex.Message}");
            }
        }

        private static void SaveDiaryState(DateTime date, string text)
        {
            try
            {
                var day = GetDayState(date);
                day.AiText = text ?? "";
                day.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveStateFile();
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"{Tag} save state failed: {ex.Message}");
            }
        }

        private static void SaveStateFile()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            var json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            File.WriteAllText(_statePath, json, new UTF8Encoding(false));
        }

        private static void LoadState()
        {
            try
            {
                if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath)) return;
                var json = File.ReadAllText(_statePath, Encoding.UTF8);
                _state = JsonConvert.DeserializeObject<DiaryState>(json);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"{Tag} load state failed: {ex.Message}");
                _state = null;
            }
        }

        // ── 文本处理 ─────────────────────────────────────────────

        private static string Get(Dictionary<string, string> map, string key)
        {
            if (map == null) return null;
            return map.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>去掉 [Emotion] 前缀与 “|||” 分隔（AIChat 的角色扮演输出格式）。</summary>
        private static string CleanAssistantText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();

            // 只取最后一段（通常是中文翻译）
            if (s.Contains("|||"))
            {
                var parts = s.Split(new[] { "|||" }, StringSplitOptions.None);
                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    if (!string.IsNullOrWhiteSpace(parts[i]))
                    {
                        s = parts[i].Trim();
                        break;
                    }
                }
            }

            // 去掉形如 [Happy] / 【开心】 的情绪标签
            while (s.Length > 2 && (s[0] == '[' || s[0] == '【'))
            {
                char close = s[0] == '[' ? ']' : '】';
                int idx = s.IndexOf(close);
                if (idx <= 0 || idx > 24) break;
                s = s.Substring(idx + 1).Trim();
            }

            // 去掉行首的 “她：/AI：” 之类前缀
            s = s.TrimStart(':', '：', ' ', '\t');
            return s.Trim();
        }

/// <summary>
        /// 去掉模型泄漏到正文里的思考块：&lt;think&gt;…&lt;/think&gt;、&lt;thinking&gt;…&lt;/thinking&gt;，
        /// 以及未闭合或多出来的单个标记。
        /// </summary>
        private static string StripReasoning(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string s = Regex.Replace(text, @"<\s*(?:think|thinking|reasoning)\b[^>]*>[\s\S]*?<\s*/\s*(?:think|thinking|reasoning)\s*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*/?\s*(?:think|thinking|reasoning)\b[^>]*>", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        private static string NormalizeDiaryText(string raw, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            s = StripReasoning(s);

            // 去掉 Markdown 代码块与标题符号
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl > 0) s = s.Substring(nl + 1);
                int end = s.LastIndexOf("```", StringComparison.Ordinal);
                if (end > 0) s = s.Substring(0, end);
                s = s.Trim();
            }

            var lines = new List<string>();
            foreach (var line in s.Split('\n'))
            {
                string l = line.TrimEnd();
                if (l.StartsWith("#")) l = l.TrimStart('#').Trim();
                if (l.StartsWith("- ")) l = l.Substring(2);
                lines.Add(l);
            }
            s = string.Join("\n", lines).Trim();

            if (maxChars > 0 && s.Length > maxChars)
            {
                // 尽量在句末截断
                int cut = maxChars;
                int best = -1;
                for (int i = Math.Min(maxChars, s.Length) - 1; i > maxChars * 0.6; i--)
                {
                    char c = s[i];
                    if (c == '。' || c == '！' || c == '？' || c == '.' || c == '!' || c == '?')
                    {
                        best = i + 1;
                        break;
                    }
                }
                if (best > 0) cut = best;
                s = s.Substring(0, cut).TrimEnd();
            }

            return s;
        }

        private static int GetMaxChars()
        {
            var cfg = PluginConfig.AutoDiaryMaxChars;
            int configured = cfg == null ? 1500 : Math.Max(200, Math.Min(8000, cfg.Value));
            bool incremental = PluginConfig.AutoDiaryIncremental == null || PluginConfig.AutoDiaryIncremental.Value;
            // 续写模式下日记会越来越长，给一个下限，避免第一版默认的 500 字把旧内容挤掉
            // 续写模式下旧内容只会越积越多，下限给高一些，避免默认值把已有段落挤掉
            return incremental ? Math.Max(configured, 2500) : configured;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static long NowEpoch()
        {
            return (long)(DateTime.Now.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
