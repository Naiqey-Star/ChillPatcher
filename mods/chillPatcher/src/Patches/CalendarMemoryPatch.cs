using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChillPatcher.Patches
{
    /// <summary>
    /// 把 <see cref="ChillPatcher.Integration.CalendarMemoryService"/> 的记忆块注入 AIChat 的请求。
    ///
    /// 做法：Postfix 挂在 AIChat 插件的
    ///     AIChat.Utils.LLMUtils::BuildRequestBody(AIChat.Utils.LLMRequestContext) : string
    /// 拿到已经拼好的 JSON 请求体，把记忆追加到 system 消息的 content 末尾再返回。
    /// 只改这一条消息，人设原文、聊天历史、采样参数都不动。
    ///
    /// 只对“人设请求”生效：聊天结束后 AIChat 还会用同一个方法发记忆摘要请求，
    /// 那种请求的 system 内容不是人设，这里用前缀匹配挡掉，避免把日记喂给摘要器。
    ///
    /// AIChat 是可选依赖：找不到类型/方法就安静跳过。
    /// </summary>
    internal static class CalendarMemoryPatch
    {
        private const string AIChatAssemblyName = "AIChat";
        private const string HarmonyId = "com.chillpatcher.calendarmemory";

        private static bool _installed;
        private static bool _failed;
        private static int _injections;
        private static int _skipped;
        private static long _lastErrorTicks;
        private static int _nextTryFrame;

        private static string _personaHead = "";
        private static long _personaHeadExpires;

        private const int PersonaHeadLength = 40;

        internal static bool Installed => _installed;

        internal static string DescribeState()
        {
            return $"[CalendarMemory] installed={_installed} failed={_failed} injections={_injections} skipped={_skipped}";
        }

        internal static void TryInstall()
        {
            if (_installed || _failed) return;
            if (_nextTryFrame > 0)
            {
                _nextTryFrame--;
                return;
            }
            _nextTryFrame = 180; // 约 3 秒重试一次，别在每帧做反射

            try
            {
                var asm = FindAiChatAssembly();
                if (asm == null) return;

                var utilsType = asm.GetType("AIChat.Utils.LLMUtils", false);
                var ctxType = asm.GetType("AIChat.Utils.LLMRequestContext", false);
                if (utilsType == null || ctxType == null)
                {
                    _failed = true;
                    Plugin.Log.LogWarning("[CalendarMemory] AIChat 内部类型缺失，跳过记忆注入");
                    return;
                }

                var target = utilsType.GetMethod(
                    "BuildRequestBody",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { ctxType },
                    null);

                if (target == null || target.ReturnType != typeof(string))
                {
                    _failed = true;
                    Plugin.Log.LogWarning("[CalendarMemory] 未找到 AIChat.LLMUtils.BuildRequestBody(string)，跳过记忆注入");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(typeof(CalendarMemoryPatch).GetMethod(
                        nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));

                _installed = true;
                Plugin.Log.LogInfo("[CalendarMemory] 已挂上 AIChat 请求注入点 (LLMUtils.BuildRequestBody)");
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogWarning($"[CalendarMemory] 安装注入点失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Assembly FindAiChatAssembly()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    if (string.Equals(asm.GetName().Name, AIChatAssemblyName, StringComparison.OrdinalIgnoreCase))
                        return asm;
                }
            }
            catch { }
            return null;
        }

        /// <summary>__result = 已经拼好的 JSON 请求体。</summary>
        private static string Postfix(string __result)
        {
            try
            {
                if (!_installed) return __result;
                if (string.IsNullOrEmpty(__result)) return __result;
                if (!ChillPatcher.Integration.CalendarMemoryService.Enabled) return __result;

                string memory = ChillPatcher.Integration.CalendarMemoryService.CurrentMemory;
                if (string.IsNullOrEmpty(memory)) return __result;

                JObject body;
                try { body = JObject.Parse(__result); }
                catch { return __result; }

                var messages = body["messages"] as JArray;
                if (messages == null || messages.Count == 0) return __result;

                JObject system = null;
                for (int i = 0; i < messages.Count; i++)
                {
                    var m = messages[i] as JObject;
                    if (m != null && string.Equals((string)m["role"], "system", StringComparison.OrdinalIgnoreCase))
                    {
                        system = m;
                        break;
                    }
                }

                // 没有 system 消息（例如摘要请求）就完全不碰
                if (system == null) { _skipped++; return __result; }

                var contentToken = system["content"];
                if (contentToken == null || (contentToken.Type != JTokenType.String && contentToken.Type != JTokenType.Null))
                {
                    _skipped++;
                    return __result;
                }

                string current = contentToken.Type == JTokenType.Null ? "" : (string)contentToken;

                // 只给“人设请求”注入：摘要请求的 system 不是人设
                if (!LooksLikePersona(current)) { _skipped++; return __result; }

                // 同一份 body 若被复用，先截掉上一次注入的块，保证记忆是最新的
                int cut = current.IndexOf(ChillPatcher.Integration.CalendarMemoryService.Marker, StringComparison.Ordinal);
                if (cut >= 0) current = current.Substring(0, cut).TrimEnd();

                string merged = current.Length == 0 ? memory : current + "\n\n" + memory;
                system["content"] = merged;

                _injections++;
                if (_injections == 1)
                {
                    Plugin.Log.LogInfo($"[CalendarMemory] 首次注入 {memory.Length} 字记忆到 AIChat 提示词");
                }

                return body.ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref _lastErrorTicks) > TimeSpan.TicksPerSecond * 10)
                {
                    Interlocked.Exchange(ref _lastErrorTicks, now);
                    Plugin.Log.LogWarning($"[CalendarMemory] 注入失败: {ex.GetType().Name}: {ex.Message}");
                }
                return __result;
            }
        }

        /// <summary>请求里的 system 内容是否就是 AIChat 配置中的人设（允许插件在人设后追加内容）。</summary>
        private static bool LooksLikePersona(string system)
        {
            if (string.IsNullOrEmpty(system)) return false;

            string head = GetPersonaHead();
            if (head.Length >= PersonaHeadLength)
            {
                return system.IndexOf(head, StringComparison.Ordinal) >= 0;
            }

            // 配置读不到时退化判断：人设通常很长
            return system.Length > 200;
        }

        private static string GetPersonaHead()
        {
            long now = DateTime.UtcNow.Ticks;
            if (!string.IsNullOrEmpty(_personaHead) && now < Interlocked.Read(ref _personaHeadExpires))
                return _personaHead;

            try
            {
                string persona = ChillPatcher.Integration.AIChatBridge.GetConfigValue("SystemPrompt") ?? "";
                persona = persona.Trim();
                if (persona.Length >= PersonaHeadLength)
                {
                    _personaHead = persona.Substring(0, PersonaHeadLength);
                    Interlocked.Exchange(ref _personaHeadExpires, now + TimeSpan.TicksPerMinute);
                    return _personaHead;
                }
            }
            catch { }

            Interlocked.Exchange(ref _personaHeadExpires, now + TimeSpan.TicksPerMinute);
            return "";
        }
    }
}
