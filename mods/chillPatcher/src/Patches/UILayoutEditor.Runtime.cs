using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace ChillPatcher.Patches
{
    public sealed partial class UILayoutEditor
    {
        readonly Dictionary<Button, string> buttonKeys = new Dictionary<Button, string>();
        void CreateEntry() { } // F8 only; no persistent entry icon.
        void Update()
        {
            // Drain first, even when Unity also reports F8 in this frame.
            bool layoutToggleRequested = KeyboardHookPatch.ConsumeLayoutToggleRequest();
            if (layoutToggleRequested || Input.GetKeyDown(KeyCode.F8)) SetEditing(!editing);
            if (editing && Input.GetKeyDown(KeyCode.Escape)) SetEditing(false);
            // Idle editor without overrides needs no global button discovery.
            if ((editing || positions.Count > 0) && Time.unscaledTime >= nextScan)
            {
                Scan();
                nextScan = Time.unscaledTime + 2f;
            }
            if (!selected) selected = null;
        }
        void Scan()
        {
            buttons.Clear();
            foreach (var b in Resources.FindObjectsOfTypeAll<Button>())
                if (b && b.gameObject.scene.IsValid() && !b.transform.IsChildOf(transform)
                    && b.GetComponentInParent<Canvas>()) buttons.Add(b);
            buttonKeys.Clear();
            foreach (var b in buttons) buttonKeys[b] = Key(b.transform);
            buttons.Sort((a,b) => string.CompareOrdinal(buttonKeys[a], buttonKeys[b]));
            InvalidateEditorList();
        }
        void SetEditing(bool value)
        {
            editing = value;
            if (value) { Scan(); if (!shield) CreateShield(); }
            if (shield) shield.SetActive(value);
        }
        void LateUpdate() { ApplyPositions(); }
        void ApplyPositions()
        {
            // No saved overrides: avoid hierarchy traversal and path allocations every frame.
            if (positions.Count == 0) return;
            foreach (var b in buttons)
            {
                if (!b) continue;
                Vector2 p;
                string key;
                if (!buttonKeys.TryGetValue(b, out key) || !positions.TryGetValue(key, out p)) continue;
                var r = b.transform as RectTransform;
                if (!r) continue;
                Capture(r);
                originals[r].element.ignoreLayout = true;
                r.anchoredPosition = p;
            }
        }
    }
}
