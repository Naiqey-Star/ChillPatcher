using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ChillPatcher.Patches
{
    // "Chill AI Mod" (com.username.chillaimod) is the AI backend; its own IMGUI
    // window duplicates ChillPatcher's window-manager AIChat panel. Keep the
    // backend working, silence its self-drawn window so only one AI UI is visible.
    // Applied lazily because ChillPatcher can load before AIChat does.
    internal static class SuppressAIChatWindow_Patch
    {
        private const string AiChatGuid = "com.username.chillaimod";
        private static readonly HarmonyLib.Harmony Harmony =
            new HarmonyLib.Harmony("ChillPatcher.SuppressAIChatUI");
        private static bool applied;

        internal static void TryApply()
        {
            if (applied) return;
            if (!Chainloader.PluginInfos.TryGetValue(AiChatGuid, out var info) || info?.Instance == null)
                return;
            var type = info.Instance.GetType();
            var list = new List<MethodBase>();
            System.Type[] types;
            try { types = type.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            if (types == null) return;
            foreach (var t in types)
            {
                if (t == null) continue;
                var m = AccessTools.Method(t, "OnGUI");
                if (m != null && m.DeclaringType != null) list.Add(m);
            }
            if (list.Count == 0) return;
            var prefix = new HarmonyMethod(typeof(SuppressAIChatWindow_Patch)
                .GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var m in list)
            {
                try { Harmony.Patch(m, prefix: prefix); }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[AIChatUI] patch failed for " + m.DeclaringType + ": " + ex.Message);
                }
            }
            applied = true;
            Plugin.Log.LogInfo("[AIChatUI] suppressed " + list.Count +
                " standalone AIChat window(s); its API stays available");
        }

        private static bool Prefix() => false;
    }
}
