using System;
using ChillPatcher.Integration;
using UnityEngine;

namespace ChillPatcher.Patches
{
    // 诊断用：CHILL_AICHAT_TEST=1 时自动发起一次文本对话，便于无人值守验证 TTS 播放。
    internal static class AIChatSelfTest
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("CHILL_AICHAT_TEST") == "1";
        private static bool _done;
        private static float _at;

        internal static void Tick()
        {
            if (!Enabled || _done) return;
            if (_at <= 0f) _at = Time.unscaledTime + 25f;
            if (Time.unscaledTime < _at) return;
            _done = true;
            try
            {
                string err;
                bool ok = AIChatBridge.TryStartTextConversation("voice test", "selftest", out err);
                Plugin.Log.LogInfo("[AIChatSelfTest] TryStartTextConversation ok=" + ok + " err=" + err);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[AIChatSelfTest] failed: " + ex.Message);
            }
        }
    }
}
