using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ChillPatcher.Patches
{
    // 壁纸模式下点“挂断”后窗口消失但进程残留、背景音继续响。
    // 在退出按钮处理完成后启动看门狗：先尝试正常 Quit，几秒后强制结束进程。
    [HarmonyPatch]
    internal static class ExitGameForceQuit_Patch
    {
        private static readonly MethodBase Target = FindTarget();
        private static bool _started;

        private static MethodBase FindTarget()
        {
            try
            {
                foreach (var t in typeof(Bulbul.FacilityMusic).Assembly.GetTypes())
                {
                    var m = AccessTools.Method(t, "OnClickButtonExitGameIcon");
                    if (m != null) return m;
                }
            }
            catch { }
            return null;
        }

        private static bool Prepare() => Target != null;

        private static MethodBase TargetMethod() => Target;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (_started) return;
            _started = true;
            Plugin.Log.LogInfo("[ExitFix] hang-up clicked, watchdog started");
            var th = new Thread(() =>
            {
                // 先给游戏时间播完告别动画和台词
                Thread.Sleep(20000);
                try { Plugin.Log.LogInfo("[ExitFix] forcing Application.Quit()"); } catch { }
                try { Application.Quit(); } catch { }
                Thread.Sleep(8000);
                try
                {
                    Plugin.Log.LogInfo("[ExitFix] process still alive, killing");
                    Process.GetCurrentProcess().Kill();
                }
                catch { }
            })
            { IsBackground = true, Name = "ChillPatcherExitWatchdog" };
            th.Start();
        }
    }
}
