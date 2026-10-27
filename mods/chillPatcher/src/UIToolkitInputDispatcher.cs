using System;
using BepInEx.Logging;
using ChillPatcher.Patches;
using UnityEngine;
using UnityEngine.UIElements;

namespace ChillPatcher
{
    /// <summary>
    /// 将键盘钩子 / RIME 输入分发到 UIToolkit TextField。
    /// 在 PlayerLoop Tick 中调用（早于 TMP_InputField.LateUpdate），
    /// 若 UIToolkit TextField 获焦则抢先消费队列，TMP patch 自动跳过。
    /// </summary>
    public static class UIToolkitInputDispatcher
    {
        private static ManualLogSource _log;

        /// <summary>当前帧是否有 UIToolkit TextField 获焦</summary>
        public static bool IsUIToolkitTextFieldFocused { get; private set; }

        /// <summary>Rime Context 变化事件（主线程触发，JSON 数据或 "null"）</summary>
        public static event Action<string, string> OnImeContextChanged;

        /// <summary>输入模式变化事件（主线程触发）</summary>
        public static event Action<bool> OnInputModeChanged;

        /// <summary>当前获焦的 TextField（供 IME API 读取位置）</summary>
        private static TextField _currentFocusedTextField;

        /// <summary>最后一次获焦 UIToolkit TextField 的面板坐标（跨帧缓存）</summary>
        private static UnityEngine.Rect? _lastTextFieldRect;

        /// <summary>UGUI TMP_InputField 屏幕坐标（Y-up 像素坐标），持久缓存直到失焦清除</summary>
        private static UnityEngine.Rect? _lastTMPScreenRect;

        /// <summary>preedit 期间锁定位置，避免候选窗跟随光标跳动</summary>
        private static bool _positionLocked;

        /// <summary>上次获焦的 TextField 及其时间，仅用于本次操作后的局部恢复。</summary>
        private static TextField _lastTextField;
        private static float _lastTextFieldAt;

        /// <summary>
        /// 把回车派发给当前获焦的 UIToolkit TextField（OneJS 输入框），
        /// 由页面自身的 onKeyDown 决定发送，C# 不重复实现聊天逻辑。
        /// </summary>
        internal static bool DispatchReturn()
        {
            var tf = ResolveTargetTextField();
            if (tf == null) return false;

            // 焦点落在内层编辑元素（等同点击输入框），事件本身发给 TextField，
            // 这样只触发页面自己的 onKeyDown，不会先被内层输入控件当成换行处理。
            var editor = (tf.Q<VisualElement>(TextFieldInputName) ?? (VisualElement)tf);
            editor.Focus();
            if (!TrySendKeyDownTo(tf as VisualElement, false))
                return false;

            RememberTextField(tf);
            return true;
        }

        /// <summary>
        /// Shift+回车：直接在光标处插入换行（有选区时替换选区），
        /// 不再依赖合成事件的默认行为，保证“换行一次、光标停在换行之后”。
        /// </summary>
        internal static bool InsertNewline()
        {
            var tf = ResolveTargetTextField();
            if (tf == null) return false;
            // 单行输入框（如配置项）不插入换行，避免把值弄脏
            if (!tf.multiline)
            {
                RememberTextField(tf);
                return true;
            }
            try
            {
                InsertTextAtCursor(tf, "\n");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[UIToolkitInput] InsertNewline error: {ex.Message}");
                return false;
            }
            RememberTextField(tf);
            return true;
        }

        private const string TextFieldInputName = "unity-text-input";

        /// <summary>
        /// 解析本次回车/换行的目标输入框：优先当前焦点，
        /// 其次扫描各面板焦点，最后回退到“刚刚还在用的那个输入框”（元素仍存活时）。
        /// 这样发送一次之后即使焦点瞬间抖动，下一次回车也不会被丢掉。
        /// </summary>
        // UGUI and UI Toolkit have independent focus systems. A native input
        // taking focus must invalidate the chat fallback before any dispatch.
        private static bool YieldToNativeInput()
        {
            var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (selected == null) return false;
            var tmp = selected.GetComponentInParent<TMPro.TMP_InputField>();
            var legacy = selected.GetComponentInParent<UnityEngine.UI.InputField>();
            bool nativeFocused = (tmp != null && tmp.isActiveAndEnabled && tmp.isFocused)
                || (legacy != null && legacy.isActiveAndEnabled && legacy.isFocused);
            if (!nativeFocused) return false;
            OnInputCaptureLost();
            return true;
        }

        private static TextField ResolveTargetTextField()
        {
            if (YieldToNativeInput()) return null;
            var tf = _currentFocusedTextField;
            if (tf != null && (tf as VisualElement)?.panel == null) tf = null;
            if (tf == null) tf = FindFocusedTextField();
            if (tf == null)
            {
                // 仅在“刚刚还在用这个输入框”的短窗口内回退，避免抢走用户后来切换的焦点
                var last = _lastTextField;
                if (last != null && (last as VisualElement)?.panel != null &&
                    UnityEngine.Time.unscaledTime - _lastTextFieldAt <= 2f)
                    tf = last;
            }
            return tf;
        }

        private static void RememberTextField(TextField tf)
        {
            _lastTextField = tf;
            _lastTextFieldAt = UnityEngine.Time.unscaledTime;
            _diagnoseFocusAt = UnityEngine.Time.unscaledTime + 1f;
        }

        /// <summary>
        /// 前台切到别的应用时调用：取消残留的焦点恢复，并主动释放 UI 焦点，
        /// 让输入焦点自然回到用户正在用的窗口，而不是继续留在壁纸层。
        /// </summary>
        internal static void OnInputCaptureLost()
        {
            _lastTextField = null;
            _lastTextFieldAt = 0f;
            _diagnoseFocusAt = 0f;
            _lastTextFieldRect = null;
            _positionLocked = false;
            _currentFocusedTextField = null;
            IsUIToolkitTextFieldFocused = false;
            try { BlurAllPanels(); }
            catch (Exception ex) { _log?.LogWarning($"[UIToolkitInput] blur on capture lost: {ex.Message}"); }
        }

        /// <summary>扫描各 OneJS 面板的焦点，返回其中获焦的 TextField（无则 null）。</summary>
        private static TextField FindFocusedTextField()
        {
            foreach (var kv in OneJSBridge.Instances)
            {
                var inst = kv.Value;
                if (inst == null || !inst.Enabled || !inst.IsInitialized) continue;

                var root = inst.Engine?.GetComponent<UIDocument>()?.rootVisualElement;
                var focused = root?.panel?.focusController?.focusedElement;
                var tf = FindTextField(focused);
                if (tf != null) return tf;
            }
            return null;
        }

        /// <summary>向元素派发一个 Return 按键事件（真实 UIToolkit 事件，会走页面 onKeyDown）。</summary>
        private static bool TrySendKeyDownTo(VisualElement ve, bool shift)
        {
            if (ve == null || ve.panel == null) return false;
            try
            {
                using (var evt = KeyDownEvent.GetPooled('\n', KeyCode.Return,
                    shift ? EventModifiers.Shift : EventModifiers.None))
                {
                    evt.target = ve;
                    ve.SendEvent(evt);
                }
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[UIToolkitInput] DispatchReturn error: {ex.Message}");
                return false;
            }
        }

        /// <summary>诊断：回车后 1 秒仍无焦点时，记录一次各面板焦点状态（有界，仅一次）。</summary>
        private static float _diagnoseFocusAt;

        private static void DiagnoseFocusIfDue()
        {
            if (_diagnoseFocusAt <= 0f) return;
            if (UnityEngine.Time.unscaledTime < _diagnoseFocusAt) return;
            _diagnoseFocusAt = 0f;

            var sb = new System.Text.StringBuilder("[AIChatFocus] after-enter:");
            foreach (var kv in OneJSBridge.Instances)
            {
                var doc = kv.Value?.Engine?.UIDocument;
                var panel = doc?.rootVisualElement?.panel;
                var focused = panel?.focusController?.focusedElement;
                sb.Append(' ').Append(kv.Key).Append('=')
                  .Append(focused == null ? "null" : focused.GetType().Name);
            }
            var es = UnityEngine.EventSystems.EventSystem.current;
            sb.Append(" eventSystem=").Append(es == null ? "null"
                : (es.currentSelectedGameObject == null ? "none" : es.currentSelectedGameObject.name));
            _log?.LogInfo(sb.ToString());
        }

        /// <summary>
        /// 局部焦点恢复：只在“刚才还有 TextField 获焦、随即丢失”的短时间内把焦点放回同一元素，
        /// 元素已被销毁则放弃；用户主动点击其它地方超过窗口期后不再干预。
        /// </summary>
        private static void FocusKeeper()
        {
            if (YieldToNativeInput()) return;
            DiagnoseFocusIfDue();
            if (_lastTextField == null) return;

            // 前台已经不归我们（用户切到别的窗口/点了别的应用）时绝不抢焦点
            if (!KeyboardHookPatch.IsCaptureTargetActive())
            {
                _lastTextField = null;
                return;
            }

            if (UnityEngine.Time.unscaledTime - _lastTextFieldAt > 2f)
            {
                _lastTextField = null;
                return;
            }
            try
            {
                var tf = _lastTextField;
                if ((tf as VisualElement)?.panel == null)
                {
                    _lastTextField = null;
                    return;
                }
                // 焦点要落在内层编辑元素上，Equals 点击输入框后的状态
                var editor = tf.Q<VisualElement>(TextFieldInputName) ?? (VisualElement)tf;
                if (editor.panel != null && editor.focusController?.focusedElement != editor)
                    editor.Focus();
            }
            catch
            {
                _lastTextField = null;
            }
        }
        public static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// 由 TMP_InputField_LateUpdate_Patch 调用，设置当前获焦 TMP 的屏幕坐标（Y-up 像素）。
        /// 如果位置已锁定（preedit 进行中）则不更新。
        /// </summary>
        public static void SetFocusedTMPScreenRect(UnityEngine.Rect screenRect)
        {
            if (_positionLocked) return;
            _lastTMPScreenRect = screenRect;
        }

        /// <summary>
        /// 由 TMP_InputField_LateUpdate_Patch 在 TMP 失焦时调用，清除缓存的坐标
        /// </summary>
        public static void ClearFocusedTMPScreenRect()
        {
            _lastTMPScreenRect = null;
            _positionLocked = false;
        }

        /// <summary>
        /// 锁定当前位置，preedit 开始时调用。
        /// 锁定后 SetFocusedTMPScreenRect 和 UIToolkit rect 不再更新，直到 UnlockPosition。
        /// </summary>
        public static void LockPosition()
        {
            _positionLocked = true;
        }

        /// <summary>
        /// 解锁位置，preedit 结束（提交/清空）时调用。
        /// 下次 preedit 开始时会重新捕获位置。
        /// </summary>
        public static void UnlockPosition()
        {
            _positionLocked = false;
        }

        /// <summary>
        /// 获取当前获焦输入框的面板坐标（UIToolkit 坐标系，Y-down）。
        /// 优先返回 UIToolkit TextField rect，其次返回 TMP rect（经坐标转换）。
        /// </summary>
        public static UnityEngine.Rect? GetFocusedTextFieldRect()
        {
            // 优先 UIToolkit TextField
            if (_lastTextFieldRect.HasValue)
                return _lastTextFieldRect;

            // 其次 UGUI TMP（持久缓存，失焦时由 ClearFocusedTMPScreenRect 清除）
            if (_lastTMPScreenRect.HasValue)
            {
                var r = _lastTMPScreenRect.Value;
                float scaleFactor = ComputePanelScaleFactor();
                // 屏幕像素 (Y-up) → 面板坐标 (Y-down, 按 referenceResolution 缩放)
                return new UnityEngine.Rect(
                    r.x / scaleFactor,
                    (Screen.height - r.y - r.height) / scaleFactor,
                    r.width / scaleFactor,
                    r.height / scaleFactor
                );
            }

            return null;
        }

        /// <summary>
        /// 计算 PanelSettings ScaleWithScreenSize(MatchWidthOrHeight, match=0.5) 的缩放因子
        /// </summary>
        private static float ComputePanelScaleFactor()
        {
            const float refWidth = 1920f;
            const float refHeight = 1080f;
            const float match = 0.5f;

            float logWidth = Mathf.Log(Screen.width / refWidth, 2f);
            float logHeight = Mathf.Log(Screen.height / refHeight, 2f);
            return Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, match));
        }

        /// <summary>
        /// 每帧调用。检查 UIToolkit 焦点并分发输入。
        /// 应在 TMP_InputField.LateUpdate 之前运行（即在 Update 阶段）。
        /// </summary>
        public static void Tick()
        {
            // 先判前台归属：切到别的应用时立刻释放，后续不再往壁纸层塞输入
            KeyboardHookPatch.RefreshCaptureState();

            IsUIToolkitTextFieldFocused = false;
            _currentFocusedTextField = null;

            // Yield before scanning OneJS panels: their cached focus can remain
            // set even after EventSystem has activated a native input field.
            if (YieldToNativeInput())
            {
                CheckAndEmitImeContextChanged();
                return;
            }

            if (!OneJSBridge.IsInitialized) return;

            foreach (var kvp in OneJSBridge.Instances)
            {
                var inst = kvp.Value;
                if (!inst.Enabled || !inst.IsInitialized) continue;

                var uiDoc = inst.Engine?.GetComponent<UIDocument>();
                var root = uiDoc?.rootVisualElement;
                if (root?.panel?.focusController == null) continue;

                var focused = root.panel.focusController.focusedElement;
                var tf = FindTextField(focused);
                if (tf != null)
                {
                    IsUIToolkitTextFieldFocused = true;
                    _currentFocusedTextField = tf;
                    _lastTextField = tf;
                    _lastTextFieldAt = UnityEngine.Time.unscaledTime;

                    // 缓存 TextField 的面板坐标（preedit 锁定期间不更新）
                    if (!_positionLocked)
                    {
                        var ve = tf as VisualElement;
                        if (ve?.panel != null)
                            _lastTextFieldRect = ve.worldBound;
                    }

                    DispatchInput(tf);
                    CheckAndEmitImeContextChanged();
                    return;
                }
            }

            // 无 TextField 获焦时清除缓存
            _lastTextFieldRect = null;
            FocusKeeper();
            CheckAndEmitImeContextChanged();
        }

        /// <summary>
        /// 检测 Rime Context 和输入模式是否有变化，有则触发事件推送给 JS 层
        /// </summary>
        private static void CheckAndEmitImeContextChanged()
        {
            // 检测输入模式变化
            if (KeyboardHookPatch.InputModeDirty)
            {
                KeyboardHookPatch.InputModeDirty = false;
                try
                {
                    OnInputModeChanged?.Invoke(KeyboardHookPatch.IsGameMode);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning($"[UIToolkitInputDispatcher] InputMode event error: {ex.Message}");
                }
            }

            // 检测 Rime Context 变化
            if (!KeyboardHookPatch.RimeContextDirty) return;
            KeyboardHookPatch.RimeContextDirty = false;

            if (OnImeContextChanged == null) return;

            try
            {
                var ctx = KeyboardHookPatch.GetRimeContext();
                string ctxJson = ctx != null && !string.IsNullOrEmpty(ctx.Preedit)
                    ? JSApi.JSApiHelper.ToJson(new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["preedit"] = ctx.Preedit,
                        ["cursorPos"] = ctx.CursorPos,
                        ["highlightedIndex"] = ctx.HighlightedIndex,
                        ["candidates"] = BuildCandidatesList(ctx),
                    })
                    : "null";

                var rect = GetFocusedTextFieldRect();
                string rectJson = rect.HasValue
                    ? JSApi.JSApiHelper.ToJson(new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["x"] = rect.Value.x,
                        ["y"] = rect.Value.y,
                        ["width"] = rect.Value.width,
                        ["height"] = rect.Value.height,
                    })
                    : "null";

                OnImeContextChanged.Invoke(ctxJson, rectJson);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[UIToolkitInputDispatcher] IME context event error: {ex.Message}");
            }
        }

        private static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> BuildCandidatesList(Rime.RimeContextInfo ctx)
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
            if (ctx.Candidates != null)
            {
                foreach (var c in ctx.Candidates)
                {
                    var item = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["text"] = c.Text ?? "",
                    };
                    if (!string.IsNullOrEmpty(c.Comment))
                        item["comment"] = c.Comment;
                    list.Add(item);
                }
            }
            return list;
        }

        /// <summary>
        /// 清除所有 UIToolkit panel 的焦点，使 TMP_InputField 恢复键盘注入。
        /// </summary>
        private static void BlurAllPanels()
        {
            foreach (var kvp in OneJSBridge.Instances)
            {
                var inst = kvp.Value;
                if (!inst.Enabled || !inst.IsInitialized) continue;

                var uiDoc = inst.Engine?.GetComponent<UIDocument>();
                var root = uiDoc?.rootVisualElement;
                var focused = root?.panel?.focusController?.focusedElement;
                if (focused is UnityEngine.UIElements.VisualElement ve)
                    ve.Blur();
            }
        }

        /// <summary>
        /// 从焦点元素出发向上查找 TextField。
        /// UIToolkit 可能将焦点设在 TextField 的内部 TextInput 子元素上。
        /// </summary>
        private static TextField FindTextField(Focusable focused)
        {
            if (focused == null) return null;
            if (focused is TextField tf) return tf;

            // 向上遍历查找 TextField 父元素
            var ve = focused as VisualElement;
            while (ve != null)
            {
                if (ve is TextField parentTf) return parentTf;
                ve = ve.parent;
            }
            return null;
        }

        private static void DispatchInput(TextField tf)
        {
            try
            {
                // 检查 Rime preedit 状态，管理位置锁定
                var rimeCtx = KeyboardHookPatch.GetRimeContext();
                bool hasPreedit = rimeCtx != null && !string.IsNullOrEmpty(rimeCtx.Preedit);
                if (hasPreedit && !_positionLocked)
                {
                    LockPosition();
                }
                else if (!hasPreedit && _positionLocked)
                {
                    UnlockPosition();
                }

                // 1. Rime 提交文本
                string commit = KeyboardHookPatch.GetCommittedText();
                if (!string.IsNullOrEmpty(commit))
                {
                    InsertTextAtCursor(tf, commit);
                    // 提交后解锁位置，下次 preedit 重新捕获
                    UnlockPosition();
                    return;
                }

                // 2. 简单队列字符
                string input = KeyboardHookPatch.GetAndClearInputBuffer();
                if (!string.IsNullOrEmpty(input))
                {
                    foreach (char c in input)
                    {
                        if (c == '\b')
                        {
                            HandleBackspace(tf);
                        }
                        else if (c == '\n')
                        {
                            // 向 UIToolkit 派发 KeyDown 事件，让 JS onKeyDown 能收到 Enter
                            DispatchReturnKeyEvent(tf);
                        }
                        else
                        {
                            InsertTextAtCursor(tf, c.ToString());
                        }
                    }
                }

                // 3. 导航键
                uint? navKey = KeyboardHookPatch.GetNavigationKey();
                while (navKey.HasValue)
                {
                    HandleNavigationKey(tf, navKey.Value);
                    navKey = KeyboardHookPatch.GetNavigationKey();
                }

                // 4. 剪贴板操作 (Ctrl+V/A/C/X)
                string pasteText;
                var clipAction = KeyboardHookPatch.GetClipboardAction(out pasteText);
                while (clipAction.HasValue)
                {
                    switch (clipAction.Value)
                    {
                        case KeyboardHookPatch.ClipAction.Paste:
                            if (!string.IsNullOrEmpty(pasteText))
                                InsertTextAtCursor(tf, pasteText);
                            break;
                        case KeyboardHookPatch.ClipAction.SelectAll:
                            tf.SelectAll();
                            break;
                        case KeyboardHookPatch.ClipAction.Copy:
                            CopySelectionToClipboard(tf);
                            break;
                        case KeyboardHookPatch.ClipAction.Cut:
                            CopySelectionToClipboard(tf);
                            DeleteSelection(tf);
                            break;
                    }
                    clipAction = KeyboardHookPatch.GetClipboardAction(out pasteText);
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[UIToolkitInput] Dispatch error: {ex.Message}");
            }
        }

        private static void InsertTextAtCursor(TextField tf, string text)
        {
            var curText = tf.value ?? "";
            int cursor = GetCursorIndex(tf, curText.Length);
            int select = GetSelectIndex(tf, cursor);

            // 有选区时先删除选中文本
            if (cursor != select)
            {
                int start = Math.Min(cursor, select);
                int end = Math.Max(cursor, select);
                curText = curText.Remove(start, end - start);
                cursor = start;
            }

            SetTextAndCaret(tf, curText.Insert(cursor, text), cursor + text.Length);
        }

        /// <summary>
        /// 写入文本并同步给 JS 层。
        /// B 窗口是受控输入框（value={prompt}），只改原生 value 的话 Preact 状态会滞后，
        /// 导致下次发送读到旧文本，因此这里补发一次 ChangeEvent 让两边一致。
        /// </summary>
        private static void SetTextAndCaret(TextField tf, string newText, int caret)
        {
            string oldText = tf.value ?? "";
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                tf.value = newText;
                if (!string.Equals(tf.value ?? "", newText, StringComparison.Ordinal))
                    tf.SetValueWithoutNotify(newText);
                NotifyTextChanged(tf, oldText, newText);
            }

            int max = (tf.value ?? "").Length;
            if (caret < 0) caret = 0;
            if (caret > max) caret = max;
            tf.cursorIndex = caret;
            tf.selectIndex = caret;
        }

        private static void NotifyTextChanged(TextField tf, string oldText, string newText)
        {
            try
            {
                using (var evt = ChangeEvent<string>.GetPooled(oldText, newText))
                {
                    evt.target = tf;
                    tf.SendEvent(evt);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[UIToolkitInput] notify change failed: {ex.Message}");
            }
        }

        private static void HandleBackspace(TextField tf)
        {
            var curText = tf.value ?? "";
            if (string.IsNullOrEmpty(curText)) return;

            int cursor = GetCursorIndex(tf, curText.Length);
            int select = GetSelectIndex(tf, cursor);

            if (cursor != select)
            {
                // 删除选区
                int start = Math.Min(cursor, select);
                int end = Math.Max(cursor, select);
                SetTextAndCaret(tf, curText.Remove(start, end - start), start);
            }
            else if (cursor > 0)
            {
                // 删除光标前一个字符
                SetTextAndCaret(tf, curText.Remove(cursor - 1, 1), cursor - 1);
            }
        }

        private static void HandleNavigationKey(TextField tf, uint vk)
        {
            var curText = tf.value ?? "";
            int cursor = GetCursorIndex(tf, curText.Length);

            switch (vk)
            {
                case 0x25: // Left
                    if (cursor > 0)
                    {
                        tf.cursorIndex = cursor - 1;
                        tf.selectIndex = cursor - 1;
                    }
                    break;
                case 0x27: // Right
                    if (cursor < curText.Length)
                    {
                        tf.cursorIndex = cursor + 1;
                        tf.selectIndex = cursor + 1;
                    }
                    break;
                case 0x24: // Home
                    tf.cursorIndex = 0;
                    tf.selectIndex = 0;
                    break;
                case 0x23: // End
                    tf.cursorIndex = curText.Length;
                    tf.selectIndex = curText.Length;
                    break;
                case 0x2E: // Delete
                    if (cursor < curText.Length)
                        SetTextAndCaret(tf, curText.Remove(cursor, 1), cursor);
                    break;
            }
        }

        /// <summary>将 TextField 选中文本复制到系统剪贴板</summary>
        private static void CopySelectionToClipboard(TextField tf)
        {
            var curText = tf.value ?? "";
            int cursor = GetCursorIndex(tf, curText.Length);
            int select = GetSelectIndex(tf, cursor);
            if (cursor == select) return;

            int start = Math.Min(cursor, select);
            int end = Math.Max(cursor, select);
            string selected = curText.Substring(start, end - start);
            GUIUtility.systemCopyBuffer = selected;
        }

        /// <summary>删除 TextField 选中文本（不影响剪贴板）</summary>
        private static void DeleteSelection(TextField tf)
        {
            var curText = tf.value ?? "";
            int cursor = GetCursorIndex(tf, curText.Length);
            int select = GetSelectIndex(tf, cursor);
            if (cursor == select) return;

            int start = Math.Min(cursor, select);
            int end = Math.Max(cursor, select);
            SetTextAndCaret(tf, curText.Remove(start, end - start), start);
        }

        private static void DispatchReturnKeyEvent(TextField tf)
        {
            try
            {
                var ve = tf as VisualElement;
                if (ve?.panel == null) return;

                using (var evt = KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None))
                {
                    evt.target = ve;
                    ve.SendEvent(evt);
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[UIToolkitInput] DispatchReturnKeyEvent error: {ex.Message}");
            }
        }

        private static int GetCursorIndex(TextField tf, int maxLen)
        {
            int idx = tf.cursorIndex;
            if (idx < 0) idx = 0;
            if (idx > maxLen) idx = maxLen;
            return idx;
        }

        private static int GetSelectIndex(TextField tf, int fallback)
        {
            int idx = tf.selectIndex;
            int maxLen = (tf.value ?? "").Length;
            if (idx < 0) idx = 0;
            if (idx > maxLen) idx = maxLen;
            return idx;
        }
    }
}
