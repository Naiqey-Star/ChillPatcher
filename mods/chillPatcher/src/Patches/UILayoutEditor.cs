using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    // Attached independently of the optional automatic rearrangement.
    public sealed partial class UILayoutEditor : MonoBehaviour
    {
        static UILayoutEditor instance;
        readonly List<Button> buttons = new List<Button>();
        readonly Dictionary<string, Vector2> positions = new Dictionary<string, Vector2>();
        readonly Dictionary<RectTransform, Original> originals = new Dictionary<RectTransform, Original>();
        sealed class Original
        {
            public Vector2 position;
            public LayoutElement element;
            public bool added, ignored;
        }
        bool editing;
        float nextScan;
        Button selected;
        GameObject shield;
        Rect window = new Rect(60, 60, 510, 560);
        Vector2 scroll;
        string filter = "", x = "0", y = "0", status = "";
        public static void Ensure()
        {
            if (instance) { instance.nextScan = Time.unscaledTime + 1f; return; }
            var go = new GameObject("ChillPatcher_LayoutEditor");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<UILayoutEditor>();
        }
        void Awake()
        {
            instance = this;
            Load();
            CreateEntry();
            nextScan = Time.unscaledTime + 1f;
        }
        static bool Finite(float n) { return !float.IsNaN(n) && !float.IsInfinity(n); }
        // Names plus same-name sibling ordinal avoid unrelated sibling insertions.
        static string Key(Transform t)
        {
            string path = "";
            for (var p = t; p; p = p.parent)
            {
                int ordinal = 0;
                if (p.parent) for (int i = 0; i < p.GetSiblingIndex(); i++)
                    if (p.parent.GetChild(i).name == p.name) ordinal++;
                path = "/" + Uri.EscapeDataString(p.name) + "[" + ordinal + "]" + path;
            }
            return t.gameObject.scene.name + path;
        }
    }
}
