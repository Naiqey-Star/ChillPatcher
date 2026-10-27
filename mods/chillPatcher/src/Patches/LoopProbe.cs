using System;
using System.Reflection;
using System.Text;
using Bulbul;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    // Diagnostic (CHILL_LOOP_PROBE=1): print loop/repeat related state so the
    // existing loop button semantics can be identified before changing them.
    internal static class LoopProbe
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("CHILL_LOOP_PROBE") == "1";
        private static float _nextAt;
        private static bool _listed;
        private static readonly bool LoopTest =
            Environment.GetEnvironmentVariable("CHILL_LOOP_TEST") == "1";
        private static bool _loopTestDone;
        private static float _loopTestAt;

        private static void ListLoopButtons()
        {
            if (_listed) return;
            _listed = true;
            var log = new StringBuilder("[LoopProbe] buttons:");
            foreach (var b in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
            {
                if (b == null) continue;
                var n = b.name;
                if (n.IndexOf("loop", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("repeat", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("shuffle", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var rt = b.transform as RectTransform;
                var pos = rt != null ? rt.position.ToString() : "?";
                log.Append(" [").Append(n).Append('@').Append(pos).Append(']');
            }
            Plugin.Log.LogInfo(log.ToString());
        }

        internal static void Tick(FacilityMusic fm)
        {
            if (!Enabled || fm == null) return;
            if (Time.unscaledTime < _nextAt) return;
            _nextAt = Time.unscaledTime + 2f;
            ListLoopButtons();
            if (LoopTest && !_loopTestDone)
            {
                if (_loopTestAt <= 0f) _loopTestAt = Time.unscaledTime + 15f;
                if (Time.unscaledTime >= _loopTestAt)
                {
                    _loopTestDone = true;
                    try
                    {
                        var svc = fm.MusicService;
                        if (svc != null && svc.PlayingMusic != null)
                        {
                            svc.IsRepeatOneMusic = true;
                            svc.SetMusicProgress(0.995f);
                            Plugin.Log.LogInfo("[LoopTest] repeatOne=on, sought to 99.5%");
                        }
                        else Plugin.Log.LogInfo("[LoopTest] nothing playing");
                    }
                    catch (Exception ex) { Plugin.Log.LogInfo("[LoopTest] ERR " + ex.Message); }
                }
            }
            var sb = new StringBuilder("[LoopProbe]");
            try
            {
                var svc = fm.MusicService;
                if (svc != null)
                {
                    sb.Append(" repeatOne=").Append(svc.IsRepeatOneMusic);
                    foreach (var p in svc.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        var n = p.Name;
                        if (n.IndexOf("loop", StringComparison.OrdinalIgnoreCase) < 0 &&
                            n.IndexOf("repeat", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object v;
                        try { v = p.GetValue(svc, null); } catch { continue; }
                        sb.Append(' ').Append(n).Append('=').Append(v);
                    }
                }
                foreach (var f in fm.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.Name.IndexOf("loop", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object v;
                    try { v = f.GetValue(fm); } catch { continue; }
                    if (v is Button b)
                    {
                        var img = b.GetComponent<Image>();
                        sb.Append(" field:").Append(f.Name)
                          .Append(" active=").Append(b.gameObject.activeInHierarchy)
                          .Append(" color=").Append(img != null ? img.color.ToString() : "null");
                    }
                    else if (v is Image i)
                    {
                        sb.Append(" field:").Append(f.Name).Append(" sprite=")
                          .Append(i.sprite != null ? i.sprite.name : "null")
                          .Append(" color=").Append(i.color.ToString());
                    }
                }
            }
            catch (Exception ex) { sb.Append(" ERR ").Append(ex.Message); }
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
