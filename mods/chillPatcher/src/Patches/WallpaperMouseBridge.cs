using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    // Wallpaper Engine renders the game behind the desktop, so the game window is
    // never focused and Unity never receives mouse input. This bridge polls the real
    // cursor / left button and dispatches UGUI pointer events directly.
    public sealed class WallpaperMouseBridge : MonoBehaviour
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000L;

        private static WallpaperMouseBridge instance;
        // Diagnostic override so the bridge can be exercised outside Wallpaper Engine.
        private static readonly bool ForceActive =
            Environment.GetEnvironmentVariable("CHILL_FORCE_WALLPAPER_MOUSE") == "1";
        private readonly List<RaycastResult> hits = new List<RaycastResult>();
        private bool pressed;
        private int wheelAccum;
        private int wheelDispatchLogged;
        private bool initialized;
        private bool styleApplied;
        private Vector2 lastPos;
        private GameObject pressTarget;
        private bool loggedActive;

        public static void Ensure()
        {
            if (instance != null) return;
            // Wallpaper mode runs unfocused; without this Unity stops ticking and
            // neither the wallpaper nor the bridge updates.
            Application.runInBackground = true;
            var host = new GameObject("ChillPatcher_WallpaperMouseBridge");
            UnityEngine.Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<WallpaperMouseBridge>();
        }

        internal static void Tick()
        {
            Ensure();
            if (instance == null) return;
            instance.Step();
        }

        private void LateUpdate()
        {
            // F9 is handled by the keyboard hook only (KeyboardHookPatch.UiTogglePending);
            // handling it here as well made a single press toggle twice.
            Probe();
            Step();
        }

        // uiHidden = 用户按 F9 的意图；autoHidden = 原生面板打开导致的临时隐藏。
        // 实际可见性 = 两者皆未隐藏，所以关掉原生面板只会恢复到用户原本的选择。
        private static bool uiHidden;
        private static bool autoHidden;
        private static bool appliedHidden;
        private static bool appliedOnce;

        internal static void ConsumeUiToggle()
        {
            if (!KeyboardHookPatch.UiTogglePending) return;
            KeyboardHookPatch.UiTogglePending = false;
            ToggleUi();
        }

        // The OneJS widget panels are expensive to keep on screen; F9 hides them so
        // the game returns to its normal frame rate without restarting.
        internal static void ToggleUi()
        {
            uiHidden = !uiHidden;
            ApplyPanelVisibility();
        }

        /// <summary>
        /// 原生操作面板（歌曲列表 / 日历 / 笔记 / 设置等）打开时临时收起叠加面板，
        /// 全部关闭后恢复。状态来自 NativePanelWatch 对游戏自身 IsActive 的只读汇总。
        /// </summary>
        internal static void TickPanelAutoHide()
        {
            NativePanelWatch.Tick();
            bool want = NativePanelWatch.AnyOpen;
            if (want == autoHidden) return;
            autoHidden = want;
            ApplyPanelVisibility();
        }

        private static void ApplyPanelVisibility()
        {
            bool hide = uiHidden || autoHidden;
            if (appliedOnce && hide == appliedHidden) return;
            appliedOnce = true;
            appliedHidden = hide;
            OneJSBridge.TickPaused = hide;
            int n = 0;
            foreach (var kv in OneJSBridge.Instances)
            {
                if (kv.Value == null) continue;
                kv.Value.SetPanelVisible(!hide);
                n++;
            }
            string reason = autoHidden ? ("native panel: " + NativePanelWatch.OpenPanel)
                : (uiHidden ? "F9" : "restored");
            Plugin.Log.LogInfo("[UI] widget panels " + (hide ? "hidden" : "shown") +
                " (" + n + "), reason=" + reason);
        }

        // Diagnostic frame-time logger, enabled with CHILL_FPS_PROBE=1.
        private static readonly bool FpsProbe =
            Environment.GetEnvironmentVariable("CHILL_FPS_PROBE") == "1";
        private static int probeFrames;
        private static float probeTime;
        private static float probeNext;

        private static void Probe()
        {
            if (!FpsProbe) return;
            probeFrames++;
            probeTime += Time.unscaledDeltaTime;
            if (Time.unscaledTime < probeNext) return;
            probeNext = Time.unscaledTime + 5f;
            float avg = probeTime / Mathf.Max(1, probeFrames);
            Plugin.Log.LogInfo("[FPS] avgFrameMs=" + (avg * 1000f).ToString("F1") +
                " fps=" + (avg > 0f ? (1f / avg).ToString("F1") : "0") +
                " frames=" + probeFrames + " gc=" + GC.CollectionCount(0) +
                " mem=" + GC.GetTotalMemory(false) / 1024 + "KB");
            probeFrames = 0;
            probeTime = 0f;
        }

        private static IntPtr cachedWindow;
        private static float windowCheckAt;

        private static IntPtr GameWindow()
        {
            // Process.MainWindowHandle enumerates all top-level windows, so cache it.
            if (cachedWindow != IntPtr.Zero && Time.unscaledTime < windowCheckAt)
                return cachedWindow;
            windowCheckAt = Time.unscaledTime + 2f;
            try
            {
                var h = Process.GetCurrentProcess().MainWindowHandle;
                if (h != IntPtr.Zero) cachedWindow = h;
            }
            catch { }
            return cachedWindow;
        }

        private static bool DesktopIsForeground()
        {
            var cls = new System.Text.StringBuilder(256);
            GetClassName(GetForegroundWindow(), cls, 256);
            var s = cls.ToString();
            return s == "Progman" || s == "WorkerW" || s == "SysListView32";
        }

        private void Step()
        {
            if (!PluginConfig.EnableWallpaperEngineMode.Value) { Reset(); return; }
            var hwnd = GameWindow();
            if (hwnd == IntPtr.Zero) { Reset(); return; }
            // Wallpaper mode: stop the wallpaper window from stealing focus when the
            // desktop is clicked, otherwise every click would pull it out of the
            // wallpaper layer instead of reaching the UI.
            if (!styleApplied)
            {
                styleApplied = true;
                var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((style & WS_EX_NOACTIVATE) == 0)
                {
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE));
                    Plugin.Log.LogInfo("[WallpaperMouse] window set to non-activating");
                }
            }
            // Focused window: Unity's own input module already handles everything.
            if (GetForegroundWindow() == hwnd) { Reset(); return; }
            if (!ForceActive && !DesktopIsForeground()) { Reset(); return; }
            Vector2 pos;
            if (!CursorInClient(hwnd, out pos)) { Reset(); return; }
            var es = EventSystem.current;
            if (es == null) return;
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (!loggedActive)
            {
                loggedActive = true;
                Plugin.Log.LogInfo("[WallpaperMouse] bridge active at " + pos);
            }
            // 滚轮：壁纸层下 Unity 收不到，由鼠标钩子累积、这里按 Unity 的槽位语义转发
            // 给光标下的滚轮处理器（ScrollRect.OnScroll / 自定义 IScrollHandler）。
            int wheel = KeyboardHookPatch.ConsumeWheelDelta();
            if (wheel != 0)
            {
                wheelAccum += wheel;
                // 防御：异常设备可能一直发小增量，避免累积值无限增长
                if (wheelAccum > 2400) wheelAccum = 1200;
                else if (wheelAccum < -2400) wheelAccum = -1200;
                int notches = wheelAccum / 120;
                if (notches != 0)
                {
                    wheelAccum -= notches * 120;
                    DispatchScroll(es, pos, notches);
                }
                return;
            }

            // First frame after gaining desktop foreground: adopt the current button
            // state instead of treating a stale press as a new click.
            if (!initialized)
            {
                initialized = true;
                pressed = down;
                lastPos = pos;
                return;
            }
            // Never double-dispatch a click Unity already processed itself.
            bool unitySees = Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0);
            if (down && !pressed)
            {
                pressed = true;
                lastPos = pos;
                if (!unitySees)
                    pressTarget = Dispatch(es, pos, PointerEventData.FramePressState.Pressed);
            }
            else if (!down && pressed)
            {
                pressed = false;
                if (!unitySees)
                    Dispatch(es, pos, PointerEventData.FramePressState.Released);
                pressTarget = null;
            }
            else if (down && pressed && (pos - lastPos).sqrMagnitude > 4f)
            {
                lastPos = pos;
                Drag(es, pos);
            }
        }

        private void Reset()
        {
            pressed = false;
            pressTarget = null;
            initialized = false;
            wheelAccum = 0;
        }

        private bool CursorInClient(IntPtr hwnd, out Vector2 pos)
        {
            pos = default;
            RECT rc;
            if (!GetClientRect(hwnd, out rc)) return false;
            POINT p;
            if (!GetCursorPos(out p)) return false;
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) return false;
            int cx = p.X - origin.X;
            int cy = p.Y - origin.Y;
            int width = rc.Right - rc.Left;
            int height = rc.Bottom - rc.Top;
            if (width <= 0 || height <= 0) return false;
            if (cx < 0 || cy < 0 || cx >= width || cy >= height) return false;
            // Unity screen space is bottom-left origin.
            pos = new Vector2(cx, height - 1 - cy);
            return true;
        }

        private GameObject Dispatch(EventSystem es, Vector2 pos, PointerEventData.FramePressState state)
        {
            var data = new PointerEventData(es)
            {
                position = pos,
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                pressPosition = pos
            };
            hits.Clear();
            es.RaycastAll(data, hits);
            if (hits.Count == 0) return null;
            data.pointerCurrentRaycast = hits[0];
            var target = hits[0].gameObject;
            if (state == PointerEventData.FramePressState.Pressed)
            {
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.initializePotentialDrag);
            }
            else
            {
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerClickHandler);
            }
            Plugin.Log.LogInfo("[WallpaperMouse] " + state + " -> " + target.name +
                " at " + pos.ToString("F0"));
            return target;
        }

        /// <summary>
        /// 把滚轮按 Unity 原生语义转发：命中光标最上层的对象，沿层级找 IScrollHandler
        /// （ScrollRect 就实现它），由组件自己决定滚动灵敏度与轴。
        /// </summary>
        private void DispatchScroll(EventSystem es, Vector2 pos, int notches)
        {
            var data = new PointerEventData(es)
            {
                position = pos,
                pointerId = -1,
                scrollDelta = new Vector2(0f, notches)
            };
            hits.Clear();
            es.RaycastAll(data, hits);
            if (hits.Count == 0) return;
            data.pointerCurrentRaycast = hits[0];
            var target = hits[0].gameObject;

            var handled = ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.scrollHandler);
            if (handled != null)
            {
                if (wheelDispatchLogged++ % 20 == 0)
                    Plugin.Log.LogInfo("[WallpaperMouse] wheel " + notches + " -> " + handled.name +
                        " at " + pos.ToString("F0"));
                return;
            }

            // 没有 IScrollHandler 时退回原生 ScrollRect 的归一化位置（仍然是游戏自己的组件）
            var sr = target.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            if (sr == null) return;
            float step = notches * 0.1f;
            if (sr.vertical)
                sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + step);
            else if (sr.horizontal)
                sr.horizontalNormalizedPosition = Mathf.Clamp01(sr.horizontalNormalizedPosition + step);
            if (wheelDispatchLogged++ % 20 == 0)
                Plugin.Log.LogInfo("[WallpaperMouse] wheel " + notches + " -> ScrollRect " + sr.name +
                    " at " + pos.ToString("F0"));
        }

        private void Drag(EventSystem es, Vector2 pos)
        {
            if (pressTarget == null) return;
            var data = new PointerEventData(es)
            {
                position = pos,
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                dragging = true
            };
            data.delta = pos - lastPos;
            ExecuteEvents.ExecuteHierarchy(pressTarget, data, ExecuteEvents.dragHandler);
        }
    }
}
