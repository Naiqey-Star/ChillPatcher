using HarmonyLib;
using Bulbul;

namespace ChillPatcher.Patches
{
    // Drives the bottom-right icon row from a game method that runs every frame,
    // so the layout survives scene transitions.
    [HarmonyPatch]
    internal static class RightBottomRow_Patch
    {
        [HarmonyPatch(typeof(FacilityMusic), nameof(FacilityMusic.UpdateFacility))]
        [HarmonyPostfix]
        private static void UpdateFacility_Postfix(FacilityMusic __instance)
        {
            LoopProbe.Tick(__instance);
            RightBottomRowLayout.Tick();
            WallpaperMouseBridge.Tick();
            WallpaperMouseBridge.ConsumeUiToggle();
            WallpaperMouseBridge.TickPanelAutoHide();
            RimeCandidateOverlay.Ensure();
            AudioProbe.Ensure();
            AIChatSelfTest.Tick();
            KeyboardHookPatch.HealthCheck();
            AIChatEnterHandler.Tick();
            ChillPatcher.Integration.AutoDiaryService.Tick();
            ChillPatcher.Integration.CalendarMemoryService.Tick();
            // SuppressAIChatWindow_Patch.TryApply(); // 暂时关闭，验证是否影响语音播放
        }
    }
}
