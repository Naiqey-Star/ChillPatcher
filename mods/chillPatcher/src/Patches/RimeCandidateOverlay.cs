using UnityEngine;

namespace ChillPatcher.Patches
{
    // Draws the Rime preedit + candidate list directly on the game screen.
    // Works even when the OneJS widget panels are hidden (F9) or failed to load,
    // which is why typing felt like "blind typing" before.
    public sealed class RimeCandidateOverlay : MonoBehaviour
    {
        private static RimeCandidateOverlay instance;
        private static readonly bool SelfTest =
            System.Environment.GetEnvironmentVariable("CHILL_RIME_SELFTEST") == "1";
        private static bool selfTestDone;
        private float selfTestAt;

        private void Update()
        {
            if (!SelfTest || selfTestDone) return;
            if (selfTestAt <= 0f) selfTestAt = Time.unscaledTime + 12f;
            if (Time.unscaledTime < selfTestAt) return;
            selfTestDone = true;
            KeyboardHookPatch.DebugTypeAscii("nihao");
            Plugin.Log.LogInfo("[RimeSelfTest] typed nihao");
        }
        private GUIStyle _box;
        private GUIStyle _preedit;
        private GUIStyle _candidate;
        private GUIStyle _active;
        private Texture2D _background;

        public static void Ensure()
        {
            if (instance != null) return;
            var host = new GameObject("ChillPatcher_RimeCandidateOverlay");
            Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<RimeCandidateOverlay>();
        }

        private void OnGUI()
        {
            if (!PluginConfig.EnableRimeInputMethod.Value) return;
            var ctx = KeyboardHookPatch.GetRimeContext();
            if (ctx == null) return;
            bool hasPreedit = !string.IsNullOrEmpty(ctx.Preedit);
            bool hasCandidates = ctx.Candidates != null && ctx.Candidates.Count > 0;
            if (!hasPreedit && !hasCandidates) return;

            EnsureStyles();

            const float pad = 8f;
            float lineH = 22f;
            int count = hasCandidates ? Mathf.Min(ctx.Candidates.Count, 9) : 0;
            float width = 260f;
            for (int i = 0; i < count; i++)
                width += 60f;
            width = Mathf.Min(width, Screen.width - 20f);
            float height = lineH * (count > 0 ? 2f : 1f) + pad * 2f;

            Vector2 mouse = Input.mousePosition;
            float x = mouse.x + 12f;
            float y = Screen.height - mouse.y + 18f;
            if (x + width > Screen.width - 10f) x = Screen.width - width - 10f;
            if (y + height > Screen.height - 10f) y = Screen.height - height - 10f;
            if (x < 10f) x = 10f;
            if (y < 10f) y = 10f;

            var rect = new Rect(x, y, width, height);
            GUI.DrawTexture(rect, _background);
            GUI.Box(rect, GUIContent.none, _box);

            if (hasPreedit)
            {
                GUI.Label(new Rect(x + pad, y + pad, width - pad * 2f, lineH), ctx.Preedit, _preedit);
            }

            if (count > 0)
            {
                float cx = x + pad;
                float cy = y + pad + (hasPreedit ? lineH : 0f);
                for (int i = 0; i < count; i++)
                {
                    var c = ctx.Candidates[i];
                    var style = i == ctx.HighlightedIndex ? _active : _candidate;
                    string label = (i + 1) + "." + (c.Text ?? "");
                    float w = style.CalcSize(new GUIContent(label)).x + 8f;
                    if (cx + w > x + width - pad)
                    {
                        cx = x + pad;
                        cy += lineH;
                    }
                    GUI.Label(new Rect(cx, cy, w, lineH), label, style);
                    cx += w + 6f;
                }
            }
        }

        private void EnsureStyles()
        {
            if (_box != null) return;
            _background = new Texture2D(1, 1);
            _background.SetPixel(0, 0, new Color(0.06f, 0.08f, 0.12f, 0.92f));
            _background.Apply();
            _box = new GUIStyle(GUI.skin.box);
            _preedit = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = Color.white }
            };
            _candidate = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = new Color(0.85f, 0.88f, 0.95f) }
            };
            _active = new GUIStyle(_candidate)
            {
                normal = { textColor = new Color(0.4f, 0.85f, 1f) }
            };
            _active.fontStyle = FontStyle.Bold;
        }
    }
}
