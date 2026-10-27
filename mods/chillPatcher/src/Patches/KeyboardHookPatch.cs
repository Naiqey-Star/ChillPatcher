using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;
using ChillPatcher.Rime;
using ChillPatcher.Patches.Rime;

namespace ChillPatcher.Patches
{
    /// <summary>
    /// 全局键盘钩子补丁 - 用于在壁纸引擎中捕获桌面键盘输入
    /// </summary>
    public class KeyboardHookPatch
    {
        private static IntPtr hookId = IntPtr.Zero;
        private static IntPtr mouseHookId = IntPtr.Zero;
        private static LowLevelMouseProc mouseProc;
        private static int wheelDeltaPending;

        /// <summary>壁纸模式下累积的滚轮增量（120 = 一格），由主线程消费。</summary>
        internal static int ConsumeWheelDelta() => Interlocked.Exchange(ref wheelDeltaPending, 0);
        private static LowLevelKeyboardProc hookCallback;
        private static readonly Queue<char> inputQueue = new Queue<char>();
        internal static readonly Queue<string> commitQueue = new Queue<string>();
        internal static readonly Queue<uint> navigationKeyQueue = new Queue<uint>(); // 导航键队列(方向键/Delete等)
        internal static readonly Queue<char> inputQueue_internal = inputQueue;
        internal static readonly object queueLock = new object();

        /// <summary>剪贴板操作类型</summary>
        public enum ClipAction : byte { Paste, SelectAll, Copy, Cut }
        private static readonly Queue<ClipAction> clipActionQueue = new Queue<ClipAction>();
        private static string _pendingPasteText;

        /// <summary>自行跟踪 Ctrl 键状态（低级钩子中 GetAsyncKeyState 不可靠）</summary>
        private static volatile bool _ctrlHeld;

        // Hook thread owns key state; Unity Update consumes only the atomic request.
        private static bool _layoutF8Held;
        private static bool _layoutF8Captured;
        private static int _layoutTogglePending;

        internal static bool ConsumeLayoutToggleRequest()
        {
            bool requested = Interlocked.Exchange(ref _layoutTogglePending, 0) != 0;
            return requested && isGameMode && IsDesktopActive();
        }

        private static Thread hookThread;
        private static volatile bool isRunning = false;
        
        // Rime输入法引擎
        private static RimeEngine rimeEngine = null;
        private static bool useRime = false;
        private static bool _debugFirstKey = true;  // 调试标志

        // 键盘输入模式：true = 游戏模式（拦截输入到游戏），false = 桌面模式（输入到系统）
        private static bool isGameMode = true;

        // Diagnostic: force the desktop-input path so IME can be exercised in a
        // normal (focused) window. Enable with CHILL_FORCE_RIME=1.
        private static readonly bool ForceRimeForTest =
            System.Environment.GetEnvironmentVariable("CHILL_FORCE_RIME") == "1";

        /// <summary>F9 pressed while the desktop is in front; consumed on the main thread.</summary>
        internal static volatile bool UiTogglePending;

        /// <summary>回车请求：带输入会话代号，切换前台后旧请求自动失效。</summary>
        private struct ReturnRequest
        {
            public int Epoch;
            public bool Shift;
        }

        private static readonly Queue<ReturnRequest> returnQueue = new Queue<ReturnRequest>();
        private static int _returnEpoch;
        /// <summary>本次物理回车是否已处理按下，用于抑制长按自动重复。</summary>
        private static bool _enterHeld;
        /// <summary>当前前台是否仍是我们的输入目标（主线程维护）。</summary>
        private static volatile bool _captureActive = true;
        /// <summary>请求钩子线程清空 Rime 组合（组合状态由钩子线程独占）。</summary>
        internal static volatile bool RimeResetPending;

        // ── 钩子健康状态（诊断与自愈） ──────────────────────────────
        /// <summary>钩子回调最近一次被系统调用的 UTC ticks（每次按键都会刷新）。</summary>
        private static long _lastHookEventTicks;
        /// <summary>主线程请求钩子线程重装钩子（在同一线程内卸旧装新，避免双钩子互相顶掉）。</summary>
        private static int _reinstallRequested;
        private static long _lastRimeUninitWarnTicks;
        private static long _lastCallbackErrorTicks;
        private static int _hookEventCount;
        private static int _hookDeadStrikes;
        private static long _nextProbeTicks;

        /// <summary>最近一次钩子收到按键的耗时（秒），供诊断。</summary>
        internal static double SecondsSinceHookEvent
        {
            get
            {
                long last = Interlocked.Read(ref _lastHookEventTicks);
                if (last == 0) return double.MaxValue;
                return (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerSecond;
            }
        }

        internal static int HookEventCount => _hookEventCount;

        private const int MaxPendingInput = 512;
        private const int MaxPendingCommits = 32;
        private const int MaxPendingNavigation = 64;
        private const int MaxPendingClip = 16;
        private const int MaxPendingReturns = 4;

        /// <summary>钩子线程：登记一次回车（一次物理按压只登记一次）。</summary>
        private static void EnqueueReturn(bool shift)
        {
            lock (queueLock)
            {
                while (returnQueue.Count >= MaxPendingReturns) returnQueue.Dequeue();
                returnQueue.Enqueue(new ReturnRequest { Epoch = _returnEpoch, Shift = shift });
            }
        }

        /// <summary>主线程：取出本会话的一条回车请求；过期请求直接丢弃。</summary>
        internal static bool TryDequeueReturn(out bool shift)
        {
            shift = false;
            lock (queueLock)
            {
                int epoch = _returnEpoch;
                while (returnQueue.Count > 0)
                {
                    var req = returnQueue.Dequeue();
                    if (req.Epoch != epoch) continue;
                    shift = req.Shift;
                    return true;
                }
            }
            return false;
        }

        private static void EnqueueInputChar(char c)
        {
            lock (queueLock)
            {
                while (inputQueue.Count >= MaxPendingInput) inputQueue.Dequeue();
                inputQueue.Enqueue(c);
            }
        }

        private static void EnqueueCommit(string text)
        {
            lock (queueLock)
            {
                while (commitQueue.Count >= MaxPendingCommits) commitQueue.Dequeue();
                commitQueue.Enqueue(text);
            }
        }

        private static void EnqueueNavigation(uint vk)
        {
            lock (queueLock)
            {
                while (navigationKeyQueue.Count >= MaxPendingNavigation) navigationKeyQueue.Dequeue();
                navigationKeyQueue.Enqueue(vk);
            }
        }

        /// <summary>
        /// Rime Context 变化通知（由 Hook 线程 set，主线程在 Tick 中检测并消费）
        /// </summary>
        internal static volatile bool RimeContextDirty;

        /// <summary>
        /// 输入模式变化通知（由 Hook 或主线程 set，主线程 Tick 中消费）
        /// </summary>
        internal static volatile bool InputModeDirty;

        /// <summary>
        /// 当前是否为游戏输入模式
        /// </summary>
        public static bool IsGameMode => isGameMode;

        /// <summary>
        /// 获取输入模式（供 JS API 调用）
        /// </summary>
        public static bool GetInputMode()
        {
            return isGameMode;
        }

        /// <summary>
        /// 设置输入模式（供 JS API 调用）
        /// </summary>
        public static void SetInputMode(bool gameMode)
        {
            bool changed = isGameMode != gameMode;
            isGameMode = gameMode;
            string modeName = isGameMode ? "游戏模式（输入到游戏）" : "桌面模式（输入到系统）";
            Plugin.Logger.LogInfo($"[KeyboardHook] 设置输入模式: {modeName}");
            if (changed && !gameMode)
            {
                Plugin.Logger.LogWarning("[KeyboardHook] 桌面输入模式下游戏内打字会失效（按键交给系统），" +
                                         "按 F5 或点击面板上的输入模式按钮可切回游戏模式；F9 仍然可用。");
            }
            InputModeDirty = true;
        }

        // 双缓冲 Context - 线程安全设计
        private static RimeContextInfo cachedRimeContext = null;
        private static readonly object rimeContextCacheLock = new object();
        
        // 崩溃保护和自动重启
        private static int restartCount = 0;
        private static readonly int maxRestartAttempts = 5;
        private static DateTime lastRestartTime = DateTime.MinValue;
        private static readonly TimeSpan restartCooldown = TimeSpan.FromSeconds(5);
        private static long _lastHeartbeatTicks = DateTime.Now.Ticks;
        private static readonly object restartLock = new object();
        
        // Windows API
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", CharSet = CharSet.Auto)]
        private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int ptX;
            public int ptY;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(
            uint nCount, IntPtr pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

        private const uint QS_ALLINPUT = 0x04FF;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        
        private const uint PM_REMOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        // 常量
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// 初始化键盘钩子
        /// </summary>
        public static void Initialize()
        {
            // 检查是否启用键盘钩子
            if (!PluginConfig.EnableKeyboardHook.Value)
            {
                Plugin.Logger.LogInfo("[KeyboardHook] 键盘钩子已禁用（配置: EnableKeyboardHook = false）");
                Plugin.Logger.LogInfo("[KeyboardHook] Rime输入法也不会启动");
                return;
            }

            if (hookThread != null && hookThread.IsAlive)
            {
                Plugin.Logger.LogWarning("[KeyboardHook] 钩子线程已经在运行");
                return;
            }

            // 初始化Rime引擎
            useRime = PluginConfig.EnableRimeInputMethod.Value;
            if (useRime)
            {
                try
                {
                    InitializeRime();
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Rime] 初始化失败: {ex.GetType().Name}: {ex.Message}");
                    Plugin.Logger.LogError($"[Rime] 堆栈: {ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        Plugin.Logger.LogError($"[Rime] 内部异常: {ex.InnerException.Message}");
                    }
                    Plugin.Logger.LogWarning("[Rime] 降级为简单输入模式");
                    useRime = false;
                }
            }
            else
            {
                Plugin.Logger.LogInfo("[KeyboardHook] 使用简单输入模式(未启用Rime)");
            }

            isRunning = true;
            hookThread = new Thread(HookThreadProc);
            hookThread.IsBackground = true;
            hookThread.Start();
            
            Plugin.Logger.LogInfo("[KeyboardHook] 钩子线程已启动");
        }

        /// <summary>
        /// 钩子线程过程 - 带崩溃保护和自动重启
        /// </summary>
        private static IntPtr InstallKeyboardHook()
        {
            try
            {
                hookCallback = HookCallback;
                using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    return SetWindowsHookEx(WH_KEYBOARD_LL, hookCallback, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogError("[KeyboardHook] 安装键盘钩子异常: " + ex.Message); } catch { }
                return IntPtr.Zero;
            }
        }

        private static IntPtr InstallMouseHook()
        {
            try
            {
                mouseProc = MouseHookCallback;
                using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                    return SetWindowsHookExMouse(WH_MOUSE_LL, mouseProc, GetModuleHandle(curModule.ModuleName), 0);
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogWarning("[KeyboardHook] 安装鼠标钩子失败: " + ex.Message); } catch { }
                return IntPtr.Zero;
            }
        }

        /// <summary>在钩子线程内卸旧装新（低级钩子被系统摘掉后只能这样恢复）。</summary>
        private static IntPtr Reinstall(IntPtr oldHandle)
        {
            if (oldHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(oldHandle); } catch { }
            }
            if (hookId == oldHandle) hookId = IntPtr.Zero;

            KeyEventConverter.ResetState();
            var fresh = InstallKeyboardHook();
            hookId = fresh;
            Interlocked.Exchange(ref _lastHookEventTicks, DateTime.UtcNow.Ticks);

            try
            {
                if (fresh != IntPtr.Zero) Plugin.Logger.LogWarning("[KeyboardHook] 钩子已重装");
                else Plugin.Logger.LogError("[KeyboardHook] 钩子重装失败 (Win32=" + Marshal.GetLastWin32Error() + ")");
            }
            catch { }
            return fresh;
        }

        private static void HookThreadProc()
        {
            // 句柄归本线程所有：重启/重装时绝不动其他线程的句柄，
            // 否则旧线程的 finally 会把新线程刚装好的钩子卸掉（曾导致整机输入失效）。
            IntPtr localHook = IntPtr.Zero;
            IntPtr localMouse = IntPtr.Zero;
            try
            {
                KeyEventConverter.ResetState();
                _ctrlHeld = false;
                _layoutF8Held = false;
                _layoutF8Captured = false;
                Interlocked.Exchange(ref _layoutTogglePending, 0);
                Interlocked.Exchange(ref _reinstallRequested, 0);
                Interlocked.Exchange(ref _lastHookEventTicks, DateTime.UtcNow.Ticks);

                localHook = InstallKeyboardHook();
                hookId = localHook;

                localMouse = InstallMouseHook();
                mouseHookId = localMouse;
                try { Plugin.Logger.LogInfo("[KeyboardHook] 鼠标滚轮钩子: " + (localMouse != IntPtr.Zero ? "ok" : "failed")); } catch { }

                if (localHook == IntPtr.Zero)
                {
                    try { Plugin.Logger.LogError($"[KeyboardHook] 钩子设置失败 (Win32={Marshal.GetLastWin32Error()})"); } catch { }
                    TryRestartHookThread();
                    return;
                }

                try { Plugin.Logger.LogInfo("[KeyboardHook] 钩子设置成功，开始消息循环"); } catch { }
                restartCount = 0; // 成功启动后重置计数器

                uint waitTimeout = (uint)Math.Max(1, Math.Min(200, PluginConfig.KeyboardHookInterval.Value));
                MSG msg;
                while (isRunning)
                {
                    try
                    {
                        // 更新心跳时间
                        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.Now.Ticks);

                        // 先把消息队列抽干
                        if (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                        {
                            if (msg.message == 0x0012) // WM_QUIT
                            {
                                try { Plugin.Logger.LogInfo("[KeyboardHook] 收到 WM_QUIT 消息"); } catch { }
                                break;
                            }

                            TranslateMessage(ref msg);
                            DispatchMessage(ref msg);
                            continue;
                        }

                        // 主线程判定钩子可能已被系统摘掉：就在本线程重装
                        if (Interlocked.Exchange(ref _reinstallRequested, 0) != 0)
                        {
                            localHook = Reinstall(localHook);
                        }

                        // 阻塞等待下一条消息而不是休眠：既不占 CPU，也不会给按键追加延迟
                        uint wait = MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, waitTimeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                        if (wait == 0xFFFFFFFF) Thread.Sleep((int)waitTimeout);
                    }
                    catch (Exception ex)
                    {
                        // 消息循环内的异常不应该导致整个线程崩溃
                        try { Plugin.Logger.LogError($"[KeyboardHook] 消息处理异常(已隔离): {ex.Message}"); } catch { }
                        Thread.Sleep(100); // 短暂延迟避免异常循环
                    }
                }

                try { Plugin.Logger.LogInfo("[KeyboardHook] 消息循环退出"); } catch { }
            }
            catch (ThreadAbortException)
            {
                // Unity 退出时会中止后台线程，这是正常的
                try { Plugin.Logger.LogInfo("[KeyboardHook] 线程被中止（正常退出）"); } catch { }
                Thread.ResetAbort(); // 重置中止状态，防止异常传播
            }
            catch (Exception ex)
            {
                try
                {
                    Plugin.Logger.LogError($"[KeyboardHook] 线程崩溃: {ex.GetType().Name}: {ex.Message}");
                    Plugin.Logger.LogError($"[KeyboardHook] 堆栈: {ex.StackTrace}");
                }
                catch { }

                // 尝试自动重启
                TryRestartHookThread();
            }
            finally
            {
                try
                {
                    if (localHook != IntPtr.Zero) UnhookWindowsHookEx(localHook);
                }
                catch (Exception ex)
                {
                    try { Plugin.Logger.LogError($"[KeyboardHook] 卸载钩子时异常: {ex.Message}"); } catch { }
                }
                try
                {
                    if (localMouse != IntPtr.Zero) UnhookWindowsHookEx(localMouse);
                }
                catch { }

                // 只清理自己那一份，别把新线程装好的句柄抹掉
                if (hookId == localHook) hookId = IntPtr.Zero;
                if (mouseHookId == localMouse) mouseHookId = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 清理键盘钩子 - 直接强制关闭，不需要保存状态
        /// </summary>
        public static void Cleanup()
        {
            Plugin.Logger.LogInfo("[KeyboardHook] 开始强制清理钩子...");
            
            isRunning = false;
            
            // 1. 先卸载钩子句柄
            if (hookId != IntPtr.Zero)
            {
                try
                {
                    UnhookWindowsHookEx(hookId);
            if (mouseHookId != IntPtr.Zero) { UnhookWindowsHookEx(mouseHookId); mouseHookId = IntPtr.Zero; }
                }
                catch { } // 忽略异常
                hookId = IntPtr.Zero;
            }

            // 2. 直接强制中止线程（不等待）
            if (hookThread != null && hookThread.IsAlive)
            {
                try
                {
                    hookThread.Abort();
                }
                catch { } // 忽略异常
                hookThread = null;
            }

            // 3. 直接释放Rime引擎（不管异常）
            if (rimeEngine != null)
            {
                try
                {
                    rimeEngine.Dispose();
                }
                catch { } // 忽略异常
                rimeEngine = null;
            }

            Plugin.Logger.LogInfo("[KeyboardHook] 钩子已强制清理完成");
        }

        /// <summary>
        /// 检查当前前台窗口是否是桌面
        /// </summary>
        private static bool IsDesktopActive()
        {
            return IsDesktopWindowClass(GetForegroundWindow());
        }

        /// <summary>前台窗口是否是壁纸/桌面层（Progman / WorkerW / 桌面列表）。</summary>
        private static bool IsDesktopWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow()) return true;

            StringBuilder className = new StringBuilder(256);
            GetClassName(hwnd, className, 256);
            string classNameStr = className.ToString();

            // 桌面窗口类名：Progman, WorkerW, SysListView32
            return classNameStr == "Progman" || classNameStr == "WorkerW" || classNameStr == "SysListView32";
        }

        private static int _ownProcessId = -1;

        /// <summary>本进程 PID（缓存一次，供输入归属判断使用）。</summary>
        private static int OwnProcessId
        {
            get
            {
                int pid = _ownProcessId;
                if (pid > 0) return pid;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
                catch { pid = 0; }
                _ownProcessId = pid;
                return pid;
            }
        }

        /// <summary>前台窗口是否属于本进程（窗口模式下游戏在前台）。</summary>
        private static bool IsOwnProcessWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            int own = OwnProcessId;
            return own > 0 && pid == (uint)own;
        }

        /// <summary>
        /// 键盘/剪贴板输入此刻是否仍应归本插件所有。
        /// 前台已经不是桌面、也不是本进程窗口时（例如用户切到别的应用）一律归还系统，
        /// 避免继续往壁纸层的输入框里塞字。
        /// </summary>
        internal static bool IsCaptureTargetActive()
        {
            if (ForceRimeForTest) return true;
            if (!isGameMode) return false;
            IntPtr fg = GetForegroundWindow();
            if (IsOwnProcessWindow(fg)) return true;
            return IsDesktopWindowClass(fg);
        }

        /// <summary>壁纸模式且桌面在最前：此时壁纸层收不到真实输入，需要钩子转发。</summary>
        internal static bool IsWallpaperInputActive()
        {
            if (ForceRimeForTest) return true;
            if (!isGameMode) return false;
            return IsDesktopWindowClass(GetForegroundWindow());
        }

        /// <summary>
        /// 主线程每帧调用：前台一旦离开我们的输入目标，立即丢弃残留输入、取消焦点恢复并清空组合，
        /// 这样切回时不会突然补字/补发送。
        /// </summary>
        internal static void RefreshCaptureState()
        {
            bool active = IsCaptureTargetActive();
            if (active)
            {
                _captureActive = true;
                return;
            }
            if (_captureActive)
            {
                _captureActive = false;
                DropPendingInput();
                UIToolkitInputDispatcher.OnInputCaptureLost();
            }
        }

        /// <summary>丢弃所有尚未消费的输入（不触碰输入框里已有的草稿）。</summary>
        private static void DropPendingInput()
        {
            lock (queueLock)
            {
                _returnEpoch++;
                inputQueue.Clear();
                commitQueue.Clear();
                navigationKeyQueue.Clear();
                clipActionQueue.Clear();
                returnQueue.Clear();
                _pendingPasteText = null;
            }
            Interlocked.Exchange(ref wheelDeltaPending, 0);

            // 立刻让候选窗/拼音显示消失，不等下一次按键
            try
            {
                lock (rimeContextCacheLock)
                {
                    cachedRimeContext = null;
                    RimeContextDirty = true;
                }
            }
            catch { }

            RimeResetPending = true; // 组合状态由钩子线程独占，异步重置
        }

        /// <summary>
        /// 鼠标钩子回调：只累积滚轮增量，供壁纸模式下的鼠标桥派发。
        /// </summary>
        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL && IsWallpaperInputActive())
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    if (delta != 0) Interlocked.Add(ref wheelDeltaPending, delta);
                }
            }
            catch { }
            return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// 键盘钩子回调
        /// </summary>
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 记录“钩子还活着”的时间戳，供主线程判断是否被系统摘钩
            Interlocked.Exchange(ref _lastHookEventTicks, DateTime.UtcNow.Ticks);
            _hookEventCount++;

            try
            {
                return HookCallbackCore(nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                // 异常绝不允许穿过原生回调边界（会直接打断钩子链）
                long now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref _lastCallbackErrorTicks) > TimeSpan.TicksPerSecond * 5)
                {
                    Interlocked.Exchange(ref _lastCallbackErrorTicks, now);
                    try { Plugin.Logger.LogError("[KeyboardHook] 回调异常(已隔离): " + ex.GetType().Name + ": " + ex.Message); } catch { }
                }
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            }
        }

        private static IntPtr HookCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vkCode = hookStruct.vkCode; KeyEventConverter.ObserveKey(vkCode, (int)wParam);

                // 主线程已判定前台离开我们的输入目标：由钩子线程清掉未提交的组合。
                if (RimeResetPending)
                {
                    RimeResetPending = false;
                    try
                    {
                        if (useRime && rimeEngine != null)
                        {
                            rimeEngine.ClearComposition();
                            UpdateCachedRimeContext();
                        }
                    }
                    catch { }
                }

                // 回车抬起一律复位，避免组合期间的按下/抬起错位导致后续回车失效。
                if ((vkCode == 0x0D || vkCode == 0x0A) &&
                    (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)0x0105))
                {
                    _enterHeld = false;
                }

                // 跟踪 Ctrl 修饰键状态（WM_KEYDOWN + WM_KEYUP），低级钩子中 GetAsyncKeyState 不可靠
                if (vkCode == 0x11 || vkCode == 0xA2 || vkCode == 0xA3) // VK_CONTROL / VK_LCONTROL / VK_RCONTROL
                {
                    _ctrlHeld = (wParam == (IntPtr)WM_KEYDOWN);
                }

                // Capture desktop F8 before Rime/simple input can consume it.
                // Swallow the captured press (including repeats/release) so Unity
                // cannot also toggle from the same physical key press.
                if (false && vkCode == 0x77) // F8 editor disabled; do not capture this key
                {
                    if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)0x0104)
                    {
                        if (!_layoutF8Held)
                        {
                            _layoutF8Held = true;
                            _layoutF8Captured = isGameMode && IsDesktopActive();
                            if (_layoutF8Captured)
                                Interlocked.Exchange(ref _layoutTogglePending, 1);
                        }
                        if (_layoutF8Captured) return (IntPtr)1;
                    }
                    else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)0x0105)
                    {
                        bool captured = _layoutF8Captured;
                        _layoutF8Held = false;
                        _layoutF8Captured = false;
                        if (captured) return (IntPtr)1;
                    }
                }

                if (wParam == (IntPtr)WM_KEYDOWN)
                {
                    // F5: 切换键盘输入模式（游戏模式 ↔ 桌面模式）
                    if (vkCode == 0x74) // VK_F5 = 0x74
                    {
                        isGameMode = !isGameMode;
                        string modeName = isGameMode ? "游戏模式（输入到游戏）" : "桌面模式（输入到系统）";
                        Plugin.Logger.LogInfo($"[KeyboardHook] F5切换输入模式: {modeName}");
                        InputModeDirty = true;
                        return (IntPtr)1; // 拦截 F5
                    }

                    // F6: 重新部署Rime(热重载配置)
                    if (vkCode == 0x75 && useRime && rimeEngine != null) // VK_F6 = 0x75
                    {
                        Plugin.Logger.LogInfo("[Rime] 用户按下F6,开始重新部署...");
                        rimeEngine.Redeploy();
                        return (IntPtr)1; // 拦截 F6
                    }

                    // Ctrl 组合键（剪贴板操作）—— 仅在 UIToolkit TextField 获焦且输入仍归我们时拦截
                    if (_ctrlHeld && UIToolkitInputDispatcher.IsUIToolkitTextFieldFocused && IsCaptureTargetActive())
                    {
                        if (vkCode == 0x56) // Ctrl+V: 粘贴
                        {
                            string text = GetClipboardText();
                            if (!string.IsNullOrEmpty(text))
                            {
                                lock (queueLock)
                                {
                                    _pendingPasteText = text;
                                    clipActionQueue.Enqueue(ClipAction.Paste);
                                }
                            }
                            return (IntPtr)1;
                        }
                        if (vkCode == 0x41) // Ctrl+A: 全选
                        {
                            lock (queueLock) { clipActionQueue.Enqueue(ClipAction.SelectAll); }
                            return (IntPtr)1;
                        }
                        if (vkCode == 0x43) // Ctrl+C: 复制
                        {
                            lock (queueLock) { clipActionQueue.Enqueue(ClipAction.Copy); }
                            return (IntPtr)1;
                        }
                        if (vkCode == 0x58) // Ctrl+X: 剪切
                        {
                            lock (queueLock) { clipActionQueue.Enqueue(ClipAction.Cut); }
                            return (IntPtr)1;
                        }
                    }

                    // F9 切换小组件面板：必须在“桌面模式直接放行”之前处理，
                    // 否则一旦切到桌面模式（例如误点面板上的输入模式按钮），F9 就彻底失效，
                    // 用户既打不了字也调不出面板，只能重启游戏。
                    if (vkCode == 0x78)
                    {
                        if (wParam == (IntPtr)WM_KEYDOWN) UiTogglePending = true;
                        return (IntPtr)1;
                    }

                    // 桌面模式：不拦截任何输入，让系统处理
                    if (!isGameMode)
                    {
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    // 游戏模式：只有桌面在前台时才由钩子接管输入（窗口模式交给 Unity 原生处理）
                    bool isDesktop = IsWallpaperInputActive();

                    if (isDesktop)
                    {
                        if (useRime && rimeEngine != null)
                        {

                            // 回车：无拼音时作为“发送”（Shift+回车 = 换行）
                            if (vkCode == 0x0D || vkCode == 0x0A)
                            {
                                if (!HasRimePreedit())
                                {
                                    if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)0x0104)
                                    {
                                        // 一次物理按压只登记一次，长按不重复发送
                                        if (!_enterHeld)
                                        {
                                            _enterHeld = true;
                                            EnqueueReturn(KeyEventConverter.ShiftHeld);
                                        }
                                    }
                                    return (IntPtr)1;
                                }

                                // 有组合时先让 Rime 提交/处理，不触发聊天发送
                                ProcessKeyWithRime(vkCode);
                                TMP_InputField_LateUpdate_Patch.RequestRimeUpdate();
                                return (IntPtr)1;
                            }

                            // 使用Rime处理输入
                            ProcessKeyWithRime(vkCode);

                            // 通知InputField刷新Rime显示
                            TMP_InputField_LateUpdate_Patch.RequestRimeUpdate();

                            return (IntPtr)1; // Rime模式总是拦截
                        }
                        else
                        {
                            // 简单队列模式
                            bool shouldIntercept = ProcessKeySimple(vkCode);
                            if (shouldIntercept)
                            {
                                return (IntPtr)1; // 拦截已处理的按键
                            }
                            // 未处理的按键(如方向键)传递给Unity
                        }
                    }
                }
            }

            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// 使用Rime处理按键 - 带异常隔离和 Context 缓存更新
        /// </summary>
        private static void ProcessKeyWithRime(uint vkCode)
        {
            try
            {
                // 检查Rime引擎是否健康
                if (rimeEngine == null || !rimeEngine.IsInitialized)
                {
                    // 逐键告警会在钩子线程上刷日志，最多每 30 秒提示一次
                    long now = DateTime.UtcNow.Ticks;
                    if (now - Interlocked.Read(ref _lastRimeUninitWarnTicks) > TimeSpan.TicksPerSecond * 30)
                    {
                        Interlocked.Exchange(ref _lastRimeUninitWarnTicks, now);
                        try { Plugin.Logger.LogWarning("[Rime] 引擎未初始化,降级为简单模式"); } catch { }
                    }
                    ProcessKeySimple(vkCode);
                    return;
                }
                
                // 使用 Weasel 风格的按键转换
                if (!KeyEventConverter.ConvertKeyEvent(vkCode, 0, out int keycode, out int mask))
                {
                    // 逐键告警会让低级钩子拖慢整机输入，改为不输出的静默跳过
                    return;
                }

                // 首次按键时检查状态(只保留标志重置)
                if (_debugFirstKey)
                {
                    _debugFirstKey = false;
                }

                // 处理Rime按键(使用转换后的 keycode 和 mask)
                bool processed = rimeEngine.ProcessKey(keycode, mask);
                
                // 如果Rime处理了按键,检查是否有提交
                if (processed)
                {
                    string commit = rimeEngine.GetCommit();
                    if (!string.IsNullOrEmpty(commit))
                    {
                        // 不在钩子线程打印正文，避免卡顿与隐私问题
                        EnqueueCommit(commit);
                    }
                    
                    // 更新缓存的 Context（在 Hook 线程中调用 Rime API）
                    UpdateCachedRimeContext();
                    
                    // Rime处理了,不再传递给简单模式
                    return;
                }
                
                // Rime没有处理(processed=false),传递给简单队列
                ProcessKeySimple(vkCode);
            }
            catch (AccessViolationException ex)
            {
                Plugin.Logger.LogError($"[Rime] 内存访问异常(已隔离): {ex.Message}");
                TryRestartRime();
                ProcessKeySimple(vkCode); // 降级处理
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Rime] 处理按键异常(已隔离): {ex.GetType().Name}: {ex.Message}");
                // 不重启Rime,继续尝试使用(可能只是偶发异常)
                ProcessKeySimple(vkCode); // 降级处理这个按键
            }
        }
        
        /// <summary>
        /// 更新缓存的 Rime Context（仅在 Hook 线程调用）
        /// </summary>
        private static void UpdateCachedRimeContext()
        {
            if (!useRime || rimeEngine == null)
                return;
            
            try
            {
                // 在 Hook 线程中调用 Rime API 获取最新 Context
                var newContext = rimeEngine.GetContext();
                
                // 原子替换缓存（双缓冲）
                lock (rimeContextCacheLock)
                {
                    cachedRimeContext = newContext;
                    RimeContextDirty = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Rime] 更新Context缓存异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 简单字符队列处理
        /// </summary>
        /// <returns>是否拦截该按键</returns>
        private static bool ProcessKeySimple(uint vkCode)
        {
            // 处理特殊按键
            if (vkCode == 0x08) // Backspace
            {
                EnqueueInputChar('\b');
                return true;
            }
            else if (vkCode == 0x0D) // Enter：与 Rime 模式统一走回车队列，保留 Shift 语义
            {
                EnqueueReturn(KeyEventConverter.ShiftHeld);
                return true;
            }

            // 方向键/Delete: 加入导航键队列
            if (vkCode >= 0x25 && vkCode <= 0x28) // Left, Up, Right, Down
            {
                EnqueueNavigation(vkCode);
                return true;
            }
            if (vkCode == 0x2E) // Delete
            {
                EnqueueNavigation(vkCode);
                return true;
            }

            // 复用 KeyEventConverter 转换所有可打印字符（包括 OEM 键如 -_=+;:等）
            // ConvertKeyEvent 内部使用 ToUnicodeEx，对普通字符 keycode == Unicode 码点
            if (KeyEventConverter.ConvertKeyEvent(vkCode, 0, out int keycode, out int mask))
            {
                // 跳过特殊键（ibus keycode > 0xFF00 表示功能键/修饰键）
                if (keycode > 0 && keycode < 0xFF00)
                {
                    char ch = (char)keycode;
                    if (!char.IsControl(ch))
                    {
                        EnqueueInputChar(ch);
                        return true;
                    }
                }
            }

            return false; // 未处理,不拦截
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// 从 Win32 剪贴板读取 Unicode 文本（可在钩子线程调用）
        /// </summary>
        private static string GetClipboardText()
        {
            if (!OpenClipboard(IntPtr.Zero))
                return null;
            try
            {
                IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                if (hData == IntPtr.Zero) return null;
                IntPtr pData = GlobalLock(hData);
                if (pData == IntPtr.Zero) return null;
                try
                {
                    return Marshal.PtrToStringUni(pData);
                }
                finally
                {
                    GlobalUnlock(hData);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// 获取下一个剪贴板操作（主线程调用）
        /// </summary>
        /// <param name="pasteText">如果是 Paste 操作，包含要粘贴的文本</param>
        /// <returns>剪贴板操作类型，无操作时返回 null</returns>
        public static ClipAction? GetClipboardAction(out string pasteText)
        {
            pasteText = null;
            lock (queueLock)
            {
                if (clipActionQueue.Count == 0) return null;
                var action = clipActionQueue.Dequeue();
                if (action == ClipAction.Paste)
                    pasteText = _pendingPasteText;
                return action;
            }
        }

        /// <summary>
        /// 获取并清空输入队列
        /// </summary>
        public static string GetAndClearInputBuffer()
        {
            lock (queueLock)
            {
                if (inputQueue.Count == 0)
                    return string.Empty;

                StringBuilder result = new StringBuilder();
                while (inputQueue.Count > 0)
                {
                    result.Append(inputQueue.Dequeue());
                }
                return result.ToString();
            }
        }

        /// <summary>
        /// 初始化Rime引擎 - 带完整异常保护
        /// </summary>
        private static void InitializeRime()
        {
            try
            {
                Plugin.Logger.LogInfo("[Rime] 正在初始化引擎...");
                
                // 验证结构体对齐和大小
                StructSizeValidator.ValidateStructSizes();
                StructSizeValidator.DumpFieldOffsets();
                
                string sharedData = RimeConfigManager.GetSharedDataDirectory();
                string userData = RimeConfigManager.GetUserDataDirectory();
                
                Plugin.Logger.LogInfo(RimeConfigManager.GetConfigInfo());
                
                rimeEngine = new RimeEngine();
                rimeEngine.Initialize(sharedData, userData, "rime.chill");
                
                RimeConfigManager.CopyExampleConfig();
                
                Plugin.Logger.LogInfo("[Rime] 引擎初始化成功");
            }
            catch (DllNotFoundException ex)
            {
                Plugin.Logger.LogError($"[Rime] DLL未找到: {ex.Message}");
                throw; // DLL缺失无法恢复,抛出让上层处理
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Rime] 初始化失败: {ex.GetType().Name}: {ex.Message}");
                Plugin.Logger.LogError($"[Rime] 堆栈: {ex.StackTrace}");
                if (rimeEngine != null)
                {
                    try { rimeEngine.Dispose(); } catch { }
                    rimeEngine = null;
                }
                throw; // 抛出让上层降级为简单模式
            }
        }

        /// <summary>
        /// 获取Rime上下文（preedit和候选词）
        /// 线程安全：从缓存读取，不直接调用 Rime API（避免多线程竞态）
        /// </summary>
        public static RimeContextInfo GetRimeContext()        {
            if (!useRime || rimeEngine == null)
                return null;
            
            try
            {
                // 从缓存读取（Unity 主线程）
                lock (rimeContextCacheLock)
                {
                    return cachedRimeContext;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Rime] GetRimeContext异常(已隔离): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 检查Rime是否有未完成的composition(preedit)
        /// 这个方法更轻量,不会触发复杂的context获取
        /// </summary>
        /// <summary>Diagnostic: push ASCII letters through the Rime path without OS input.</summary>
        public static void DebugTypeAscii(string text)
        {
            if (string.IsNullOrEmpty(text) || rimeEngine == null) return;
            foreach (var ch in text)
                ProcessKeyWithRime((byte)char.ToUpperInvariant(ch));
            var ctx = GetRimeContext();
            Plugin.Logger.LogInfo("[RimeSelfTest] ctx=" + (ctx == null ? "null" :
                ("preedit='" + ctx.Preedit + "' candidates=" +
                 (ctx.Candidates == null ? 0 : ctx.Candidates.Count))));
        }

        public static bool HasRimePreedit()
        {
            if (!useRime || rimeEngine == null || !rimeEngine.IsInitialized)
                return false;
            
            // 使用队列判断:如果有commit队列,说明有输入
            // 或者检查是否有按键事件正在处理
            lock (rimeContextCacheLock)
            {
                return cachedRimeContext != null && !string.IsNullOrEmpty(cachedRimeContext.Preedit);
            }
        }

        /// <summary>
        /// 获取Rime提交的文本
        /// </summary>
        public static string GetCommittedText()
        {
            lock (queueLock)
            {
                if (commitQueue.Count == 0)
                    return null;
                
                return commitQueue.Dequeue();
            }
        }

        /// <summary>
        /// 获取导航键(方向键/Delete)
        /// </summary>
        public static uint? GetNavigationKey()
        {
            lock (queueLock)
            {
                if (navigationKeyQueue.Count == 0)
                    return null;
                
                return navigationKeyQueue.Dequeue();
            }
        }

        /// <summary>
        /// 模拟 Rime 按键（用于 IME 候选词选择等）
        /// 在调用线程直接操作 Rime，之后更新 context 缓存
        /// </summary>
        public static bool SimulateRimeKey(int keycode, int mask)
        {
            if (!useRime || rimeEngine == null || !rimeEngine.IsInitialized)
                return false;

            try
            {
                bool processed = rimeEngine.ProcessKey(keycode, mask);
                if (processed)
                {
                    string commit = rimeEngine.GetCommit();
                    if (!string.IsNullOrEmpty(commit))
                    {
                        lock (queueLock)
                        {
                            commitQueue.Enqueue(commit);
                        }
                    }

                    // 更新 context 缓存
                    var newContext = rimeEngine.GetContext();
                    lock (rimeContextCacheLock)
                    {
                        cachedRimeContext = newContext;
                        RimeContextDirty = true;
                    }

                    // 通知 TMP patch 更新
                    TMP_InputField_LateUpdate_Patch.RequestRimeUpdate();
                }
                return processed;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Rime] SimulateRimeKey异常: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过索引选择 Rime 候选词（主线程调用）
        /// </summary>
        public static bool SelectRimeCandidate(int index)
        {
            if (!useRime || rimeEngine == null || !rimeEngine.IsInitialized)
                return false;

            try
            {
                bool selected = rimeEngine.SelectCandidate(index);
                if (selected)
                {
                    string commit = rimeEngine.GetCommit();
                    if (!string.IsNullOrEmpty(commit))
                    {
                        lock (queueLock)
                        {
                            commitQueue.Enqueue(commit);
                        }
                    }

                    var newContext = rimeEngine.GetContext();
                    lock (rimeContextCacheLock)
                    {
                        cachedRimeContext = newContext;
                        RimeContextDirty = true;
                    }

                    TMP_InputField_LateUpdate_Patch.RequestRimeUpdate();
                }
                return selected;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Rime] SelectRimeCandidate异常: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清空所有待处理输入（Rime 组合、字符、提交、导航、剪贴板、回车）。
        /// 只清待处理数据，不动输入框里已有的草稿。
        /// </summary>
        public static void ClearAll()
        {
            // 组合状态的清理交给独占 Rime 的钩子线程执行，避免跨线程调用
            if (useRime && rimeEngine != null)
                RimeResetPending = true;

            // 清空缓存的 Context
            try
            {
                lock (rimeContextCacheLock)
                {
                    cachedRimeContext = null;
                    RimeContextDirty = true;
                }
            }
            catch { }

            DropPendingInput();
        }
        
        /// <summary>
        /// 尝试重启钩子线程 - 带限流保护
        /// </summary>
        private static void TryRestartHookThread()
        {
            lock (restartLock)
            {
                var now = DateTime.Now;
                
                // 检查冷却时间
                if (now - lastRestartTime < restartCooldown)
                {
                    Plugin.Logger.LogWarning($"[KeyboardHook] 重启冷却中,跳过重启(剩余 {(restartCooldown - (now - lastRestartTime)).TotalSeconds:F1}s)");
                    return;
                }
                
                // 检查重启次数
                if (restartCount >= maxRestartAttempts)
                {
                    Plugin.Logger.LogError($"[KeyboardHook] 已达到最大重启次数({maxRestartAttempts}),停止自动重启");
                    isRunning = false;
                    return;
                }
                
                restartCount++;
                lastRestartTime = now;
                
                Plugin.Logger.LogWarning($"[KeyboardHook] 尝试静默重启钩子线程(第 {restartCount}/{maxRestartAttempts} 次)...");
                
                try
                {
                    // 先请旧线程自己退出（不发 PostQuitMessage：它只会投递到调用它的线程）
                    var old = hookThread;
                    isRunning = false;
                    if (old != null && old != Thread.CurrentThread && old.IsAlive)
                    {
                        old.Join(800);
                        if (old.IsAlive)
                        {
                            Plugin.Logger.LogWarning("[KeyboardHook] 旧钩子线程未在 0.8s 内退出，继续新建（旧线程的句柄由它自己回收）");
                        }
                    }

                    // 句柄已经由旧线程在 finally 里回收；这里只清空我们自己的记录
                    hookId = IntPtr.Zero;
                    mouseHookId = IntPtr.Zero;

                    // 启动新线程
                    isRunning = true;
                    var thread = new Thread(HookThreadProc);
                    thread.IsBackground = true;
                    thread.Name = "ChillPatcher.KeyboardHook";
                    hookThread = thread;
                    thread.Start();
                    
                    Plugin.Logger.LogInfo($"[KeyboardHook] 钩子线程重启成功");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[KeyboardHook] 重启失败: {ex.Message}");
                    isRunning = false;
                }
            }
        }
        
        /// <summary>
        /// 尝试重启Rime引擎 - 带限流保护
        /// </summary>
        private static void TryRestartRime()
        {
            lock (restartLock)
            {
                Plugin.Logger.LogWarning("[Rime] 检测到严重错误,尝试重启引擎...");
                
                try
                {
                    // 释放旧引擎
                    if (rimeEngine != null)
                    {
                        try { rimeEngine.Dispose(); } catch { }
                        rimeEngine = null;
                    }
                    
                    Thread.Sleep(500); // 短暂延迟
                    
                    // 重新初始化
                    InitializeRime();
                    
                    Plugin.Logger.LogInfo("[Rime] 引擎重启成功");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Rime] 重启失败,降级为简单模式: {ex.Message}");
                    useRime = false;
                    rimeEngine = null;
                }
            }
        }
        
        /// <summary>
        /// 健康检查 - 由Unity主线程每帧调用
        ///
        /// 三道防线：
        ///  1. 线程死了 → 重新起线程；
        ///  2. 心跳超时 → 让钩子线程自己重装钩子（不要另起线程，否则两个钩子互相顶掉）；
        ///  3. 钩子还“活着”但系统已经把它摘掉（低级钩子回调超时会被 Windows 静默移除）
        ///     → 用 GetAsyncKeyState 探测“物理上确实按了键，但钩子没收到”，然后重装。
        /// </summary>
        public static void HealthCheck()
        {
            if (!PluginConfig.EnableKeyboardHook.Value) return;

            try
            {
                if (!isRunning) return;

                // 1. 钩子线程存活
                if (hookThread == null || !hookThread.IsAlive)
                {
                    Plugin.Logger.LogWarning("[KeyboardHook] 检测到钩子线程已死亡,尝试重启...");
                    TryRestartHookThread();
                    return;
                }

                // 2. 心跳（线程卡死，例如 Rime 部署、日志锁等长时间阻塞）
                double stale = (DateTime.Now.Ticks - Interlocked.Read(ref _lastHeartbeatTicks)) / (double)TimeSpan.TicksPerSecond;
                if (stale > 10)
                {
                    Plugin.Logger.LogWarning($"[KeyboardHook] 检测到线程无响应(心跳超时 {stale:F1}s),请求重装钩子");
                    Interlocked.Exchange(ref _reinstallRequested, 1);
                    Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.Now.Ticks); // 避免每帧刷日志
                    return;
                }

                // 3. 钩子可能已被系统静默摘除
                ProbeHookLiveness();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[KeyboardHook] 健康检查异常: {ex.Message}");
            }
        }

        /// <summary>打字时最可能按到的键，用于判断“用户确实在按键”。</summary>
        private static readonly int[] LivenessProbeKeys =
        {
            0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x51, 0x52, 0x53, 0x54, 0x57,
            0x20, 0x0D, 0x08, 0x09, 0x1B, 0x31, 0x32, 0x33,
        };

        /// <summary>
        /// 用 GetAsyncKeyState 的“自上次调用后被按过”位做探针：
        /// 物理按键发生了，但钩子回调没刷新时间戳，就说明钩子被摘掉了，需要重装。
        /// </summary>
        private static void ProbeHookLiveness()
        {
            if (!isGameMode) return;                    // 桌面模式本来就不接管输入
            if (hookId == IntPtr.Zero) return;

            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref _nextProbeTicks)) return;
            Interlocked.Exchange(ref _nextProbeTicks, now + TimeSpan.TicksPerSecond);

            // 只有“当下确实该由我们收键”的时候才判断，切到别的应用时按键本来就不归我们
            if (!IsCaptureTargetActive()) { _hookDeadStrikes = 0; return; }

            bool pressedAny = false;
            foreach (int vk in LivenessProbeKeys)
            {
                if ((GetAsyncKeyState(vk) & 0x0001) != 0) { pressedAny = true; break; }
            }
            if (!pressedAny) { _hookDeadStrikes = 0; return; }

            double idle = SecondsSinceHookEvent;
            if (idle < 1.5) { _hookDeadStrikes = 0; return; }

            if (++_hookDeadStrikes < 2) return;
            _hookDeadStrikes = 0;

            Plugin.Logger.LogWarning($"[KeyboardHook] 检测到按键未被钩子接收（{idle:F1}s 无回调），请求重装钩子");
            Interlocked.Exchange(ref _reinstallRequested, 1);
        }
    }

    /// <summary>
    /// Patch Update - 每帧检测鼠标点击，清空输入队列；
    /// 在 EventSystem.Update 之前更新 UIToolkitEventBlocker 检测状态。
    /// </summary>
    [HarmonyPatch(typeof(EventSystem), "Update")]
    public class EventSystem_Update_Patch
    {
        static void Prefix()
        {
            // 更新 UIToolkit 拦截器（检测鼠标是否在 UIToolkit 可交互区域上）
            UIToolkitEventBlocker.Update();

            // 检测鼠标点击（左键按下）
            if (UnityEngine.Input.GetMouseButtonDown(0))
            {
                // 清空所有输入
                KeyboardHookPatch.ClearAll();
                
                // 通知InputField刷新(鼠标点击可能改变焦点)
                TMP_InputField_LateUpdate_Patch.RequestRimeUpdate();
            }
        }
    }

    /// <summary>
    /// Patch TMP_InputField 来注入键盘输入和显示Rime候选词
    /// 
    /// ✅ 使用 Publicizer 直接访问 KeyPressed 方法（消除反射开销）
    /// </summary>
    [HarmonyPatch(typeof(TMP_InputField), "LateUpdate")]
    public class TMP_InputField_LateUpdate_Patch
    {
        
        // 使用字典为每个InputField实例维护独立状态
        private static Dictionary<int, PreeditState> preeditStates = new Dictionary<int, PreeditState>();
        
        // 需要更新的InputField ID (由Hook直接设置)
        private static volatile int pendingUpdateInstanceId = -1;
        private static readonly object updateLock = new object();

        class PreeditState
        {
            public string lastPreeditDisplay = "";
            public string savedBaseText = "";
            public string savedTextAfterCaret = ""; // 光标后的文本
            public int savedCaretPosition = 0; // 进入preedit时的光标位置
            public bool inPreeditMode = false;
        }
        
        /// <summary>
        /// 通知需要更新Rime显示(由Hook线程调用)
        /// </summary>
        public static void RequestRimeUpdate()
        {
            lock (updateLock)
            {
                pendingUpdateInstanceId = -2; // -2表示全局更新(不限定InputField)
            }
        }

        static void Postfix(TMP_InputField __instance)
        {
            try
            {
                // UIToolkit TextField 获焦时，队列已被 UIToolkitInputDispatcher 消费，跳过 TMP 注入
                if (UIToolkitInputDispatcher.IsUIToolkitTextFieldFocused)
                    return;

                int instanceId = __instance.GetInstanceID();
                
                // 只在输入框激活且获得焦点时注入
                if (!__instance.isFocused)
                {
                    // 失焦时清理:preedit状态、Rime composition、所有队列
                    if (preeditStates.ContainsKey(instanceId))
                    {
                        preeditStates.Remove(instanceId);
                        
                        // 清理Rime composition和队列
                        KeyboardHookPatch.ClearAll();
                        
                        // 清除 TMP 屏幕坐标缓存
                        UIToolkitInputDispatcher.ClearFocusedTMPScreenRect();
                        
                        Plugin.Logger.LogInfo($"[InputField #{instanceId}] 失焦,已清理所有状态和队列");
                    }
                    return;
                }

                // 获取或创建该InputField的preedit状态
                if (!preeditStates.TryGetValue(instanceId, out var state))
                {
                    state = new PreeditState();
                    preeditStates[instanceId] = state;
                }

                // 缓存当前获焦 TMP 的光标屏幕坐标供 IME 候选窗定位
                try
                {
                    var textComponent = __instance.textComponent;
                    int caretPos = __instance.caretPosition;
                    
                    if (textComponent != null && textComponent.textInfo != null 
                        && textComponent.textInfo.characterCount > 0 && caretPos >= 0)
                    {
                        // 将 caretPos 限制在有效范围内
                        int charIndex = Mathf.Min(caretPos, textComponent.textInfo.characterCount - 1);
                        var charInfo = textComponent.textInfo.characterInfo[charIndex];
                        
                        // 光标位置：如果 caret 在文字后面，取字符右边缘；否则取左边缘
                        float localX = (caretPos > charIndex) ? charInfo.topRight.x : charInfo.bottomLeft.x;
                        float localY = charInfo.bottomLeft.y;
                        
                        // TMP 本地坐标 → 世界坐标 → 屏幕坐标
                        Vector3 worldPos = textComponent.transform.TransformPoint(new Vector3(localX, localY, 0));
                        
                        // 对 ScreenSpaceOverlay Canvas，worldPos 已经是屏幕像素 (Y-up)
                        // 生成一个小 rect 代表光标位置（宽=1，高=行高）
                        float lineHeight = charInfo.topRight.y - charInfo.bottomLeft.y;
                        Vector3 worldTop = textComponent.transform.TransformPoint(
                            new Vector3(localX, charInfo.topRight.y, 0));
                        float screenLineHeight = Mathf.Abs(worldTop.y - worldPos.y);
                        
                        var caretRect = new Rect(worldPos.x, worldPos.y, 1f, screenLineHeight);
                        UIToolkitInputDispatcher.SetFocusedTMPScreenRect(caretRect);
                    }
                    else
                    {
                        // fallback：使用整个输入框 rect
                        var rt = __instance.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            Vector3[] corners = new Vector3[4];
                            rt.GetWorldCorners(corners);
                            var screenRect = new Rect(
                                corners[0].x, corners[0].y,
                                corners[2].x - corners[0].x,
                                corners[1].y - corners[0].y);
                            UIToolkitInputDispatcher.SetFocusedTMPScreenRect(screenRect);
                        }
                    }
                }
                catch { /* 获焦坐标非关键路径 */ }

                // 检查是否有pending的更新请求(由Hook触发)
                bool shouldUpdateRime = false;
                lock (updateLock)
                {
                    if (pendingUpdateInstanceId == -2) // 全局更新
                    {
                        shouldUpdateRime = true;
                        pendingUpdateInstanceId = -1; // 清除pending状态
                    }
                }
                
                // 只在Hook触发更新时调用GetRimeContext(按键后更新一次)
                if (shouldUpdateRime)
                {
                    // 1. 检查Rime的preedit状态
                    var rimeContext = KeyboardHookPatch.GetRimeContext();
                
                    if (rimeContext != null && !string.IsNullOrEmpty(rimeContext.Preedit))
                    {
                        // 进入preedit模式时保存基础文本和光标位置
                        if (!state.inPreeditMode)
                        {
                            int caret = __instance.caretPosition;
                            state.savedCaretPosition = caret;
                            state.savedBaseText = __instance.text.Substring(0, caret); // 光标前的文本
                            state.savedTextAfterCaret = caret < __instance.text.Length ? __instance.text.Substring(caret) : ""; // 光标后的文本
                            state.inPreeditMode = true;
                            // 锁定候选窗位置，避免打字时跳动
                            UIToolkitInputDispatcher.LockPosition();
                            Plugin.Logger.LogInfo($"[Preedit #{instanceId}] 进入模式, caret={caret}, before='{state.savedBaseText}', after='{state.savedTextAfterCaret}'");
                        }

                        // 行内 preedit 预览已禁用，使用图形化 IME 候选面板替代
                        // 保留代码以备后续可选启用
                        #if false
                        // 生成新的preedit显示
                        string currentPreeditDisplay = rimeContext.GetPreeditWithCandidates();
                        
                        // 只在preedit显示内容变化时更新文本
                        if (currentPreeditDisplay != state.lastPreeditDisplay)
                        {
                            Plugin.Logger.LogInfo($"[Preedit #{instanceId}] 更新显示: '{state.lastPreeditDisplay}' → '{currentPreeditDisplay}'");
                            state.lastPreeditDisplay = currentPreeditDisplay;
                            // 文本 = 光标前 + preedit + 光标后
                            __instance.text = state.savedBaseText + currentPreeditDisplay + state.savedTextAfterCaret;
                            __instance.ForceLabelUpdate(); // 立即更新文本显示
                        }
                        
                        // 更新光标位置
                        int targetCaret = state.savedBaseText.Length + rimeContext.CursorPos;
                        __instance.caretPosition = targetCaret;
                        __instance.stringPosition = targetCaret;
                        __instance.selectionAnchorPosition = targetCaret;
                        __instance.selectionFocusPosition = targetCaret;
                        __instance.ForceLabelUpdate();
                        #endif
                        
                        return; // preedit进行中，不处理提交
                    }
                    else
                    {
                        // 退出preedit模式,恢复基础文本
                        if (state.inPreeditMode)
                        {
                            __instance.text = state.savedBaseText + state.savedTextAfterCaret;
                            __instance.caretPosition = state.savedCaretPosition;
                            state.lastPreeditDisplay = "";
                            state.savedBaseText = "";
                            state.savedTextAfterCaret = "";
                            state.inPreeditMode = false;
                            // 解锁候选窗位置，下次 preedit 开始时重新捕获
                            UIToolkitInputDispatcher.UnlockPosition();
                        }
                    }
                }

                // 2. 获取已提交的文本
                string commit = KeyboardHookPatch.GetCommittedText();
                if (!string.IsNullOrEmpty(commit))
                {
                    int currentCaret = __instance.caretPosition;
                    Plugin.Logger.LogInfo($"[InputField #{instanceId}] 提交前: text='{__instance.text}', caret={currentCaret}");
                    
                    // 插入到光标位置
                    __instance.text = __instance.text.Insert(currentCaret, commit);
                    __instance.ForceLabelUpdate(); // 先强制更新文本
                    
                    // 光标移动到插入文本之后
                    int targetCaret = currentCaret + commit.Length;
                    __instance.caretPosition = targetCaret;
                    __instance.stringPosition = targetCaret; // TMP特有属性
                    __instance.selectionAnchorPosition = targetCaret;
                    __instance.selectionFocusPosition = targetCaret;
                    
                    Plugin.Logger.LogInfo($"[InputField #{instanceId}] 提交后: text='{__instance.text}', caret={__instance.caretPosition}, target={targetCaret}, commit='{commit}'");
                    return;
                }

                // 3. 简单队列模式的兼容处理
                string simpleInput = KeyboardHookPatch.GetAndClearInputBuffer();
                if (!string.IsNullOrEmpty(simpleInput))
                {
                    // ✅ 直接调用 KeyPressed 处理每个字符（Publicizer 消除反射）
                    foreach (char c in simpleInput)
                    {
                        UnityEngine.Event evt = new UnityEngine.Event();
                        evt.type = UnityEngine.EventType.KeyDown;

                        if (c == '\b')
                        {
                            evt.keyCode = KeyCode.Backspace;
                            evt.character = '\0';
                        }
                        else if (c == '\n')
                        {
                            evt.keyCode = KeyCode.Return;
                            evt.character = '\n';
                        }
                        else
                        {
                            evt.keyCode = KeyCode.None;
                            evt.character = c;
                        }

                        __instance.KeyPressed(evt);
                    }

                    __instance.ForceLabelUpdate();
                }

                // 4. 处理导航键(方向键/Delete)
                uint? navKey = KeyboardHookPatch.GetNavigationKey();
                if (navKey.HasValue)
                {
                    int currentCaret = __instance.caretPosition;
                    int textLength = __instance.text.Length;

                    switch (navKey.Value)
                    {
                        case 0x25: // Left
                            if (currentCaret > 0)
                            {
                                __instance.caretPosition = currentCaret - 1;
                                __instance.stringPosition = currentCaret - 1;
                                __instance.selectionAnchorPosition = currentCaret - 1;
                                __instance.selectionFocusPosition = currentCaret - 1;
                                __instance.ForceLabelUpdate(); // 强制更新,避免闪烁延迟
                            }
                            break;

                        case 0x27: // Right
                            if (currentCaret < textLength)
                            {
                                __instance.caretPosition = currentCaret + 1;
                                __instance.stringPosition = currentCaret + 1;
                                __instance.selectionAnchorPosition = currentCaret + 1;
                                __instance.selectionFocusPosition = currentCaret + 1;
                                __instance.ForceLabelUpdate(); // 强制更新,避免闪烁延迟
                            }
                            break;

                        case 0x26: // Up
                            MoveCaretVertically(__instance, -1);
                            break;

                        case 0x28: // Down
                            MoveCaretVertically(__instance, 1);
                            break;

                        case 0x2E: // Delete
                            if (currentCaret < textLength)
                            {
                                __instance.text = __instance.text.Remove(currentCaret, 1);
                                __instance.ForceLabelUpdate();
                            }
                            break;
                    }
                }

            }
            catch (Exception ex)
            {
                // 捕获所有异常,防止崩溃Unity
                Plugin.Logger.LogError($"[InputField] LateUpdate异常(已隔离): {ex.GetType().Name}: {ex.Message}");
                Plugin.Logger.LogError($"[InputField] 堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 垂直移动光标(上下键)
        /// </summary>
        private static void MoveCaretVertically(TMP_InputField inputField, int direction)
        {
            // 确保textInfo已更新
            inputField.ForceLabelUpdate();
            var textInfo = inputField.textComponent.textInfo;
            
            if (textInfo == null || textInfo.lineCount == 0)
                return;

            int currentCaret = inputField.caretPosition;
            
            // 查找当前光标所在行
            int currentLine = -1;
            int charIndexInLine = 0;
            for (int i = 0; i < textInfo.lineCount; i++)
            {
                int lineStart = textInfo.lineInfo[i].firstCharacterIndex;
                int lineEnd = textInfo.lineInfo[i].lastCharacterIndex;
                
                if (currentCaret >= lineStart && currentCaret <= lineEnd + 1)
                {
                    currentLine = i;
                    charIndexInLine = currentCaret - lineStart;
                    break;
                }
            }

            if (currentLine == -1)
                return;

            // 计算目标行
            int targetLine = currentLine + direction;
            if (targetLine < 0 || targetLine >= textInfo.lineCount)
                return; // 超出范围

            // 获取目标行信息
            int targetLineStart = textInfo.lineInfo[targetLine].firstCharacterIndex;
            int targetLineEnd = textInfo.lineInfo[targetLine].lastCharacterIndex;
            int targetLineLength = targetLineEnd - targetLineStart + 1;

            // 尝试保持相同的列位置,如果目标行更短,则移到行尾
            int targetCaret = targetLineStart + Math.Min(charIndexInLine, targetLineLength);

            // 更新光标
            inputField.caretPosition = targetCaret;
            inputField.stringPosition = targetCaret;
            inputField.selectionAnchorPosition = targetCaret;
            inputField.selectionFocusPosition = targetCaret;
        }
    }
}
