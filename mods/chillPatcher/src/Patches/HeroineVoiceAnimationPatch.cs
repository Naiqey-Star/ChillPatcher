using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ChillPatcher.Patches
{
    // Legacy-version compatibility: EndNoVoiceTalk checks _isFinishedVoice.
    // Installed game MVID 67cf09ec-b10e-49f2-bc4c-2e04b18a9769 already passes false;
    // this transpiler leaves that version unchanged. Runtime recovery is unverified.
    // then calls ChangeEnableHeroineTalkAnimation(true). Disable the talk
    // animation on that existing completion branch instead.
    [HarmonyPatch(typeof(HeroineVoiceController), "EndNoVoiceTalk")]
    internal static class HeroineVoiceAnimationPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var toggle = AccessTools.Method(typeof(HeroineVoiceController),
                "ChangeEnableHeroineTalkAnimation", new[] { typeof(bool) });

            // Report patch-time matching only; this does not prove runtime recovery.
            int matches = 0;
            for (var i = 1; i < codes.Count; i++)
            {
                if (toggle != null && codes[i].Calls(toggle)
                    && codes[i - 1].opcode == OpCodes.Ldc_I4_1)
                {
                    // Mutate in place to preserve branch labels and blocks.
                    codes[i - 1].opcode = OpCodes.Ldc_I4_0;
                    codes[i - 1].operand = null;
                    matches++;
                }
            }

            if (matches == 0)
                Plugin.Logger.LogWarning("[HeroineVoiceAnimation] EndNoVoiceTalk: no matching true argument found; IL unchanged. Voice-loop fix is not verified.");
            else
                Plugin.Logger.LogInfo($"[HeroineVoiceAnimation] EndNoVoiceTalk: changed {matches} matching true argument(s) to false. Patch-time match only; runtime behavior is not verified.");

            return codes;
        }
    }
}
