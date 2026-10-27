using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    // Independent of EnableUIRearrange; call Ensure from plugin initialization too.
    public sealed class RightIconsCompactLayout : MonoBehaviour
    {
        private static RightIconsCompactLayout instance;
        private const string Path = "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons";
        private const string LegacyPath = "Parent/PCPlatform/Canvas/UI/MostFrontArea/RightIcons";
        private RectTransform root;
        private float nextUpdate;
        private readonly Dictionary<RectTransform, Vector3> scales = new Dictionary<RectTransform, Vector3>();
        private readonly List<RectTransform> items = new List<RectTransform>();
        private readonly Vector3[] corners = new Vector3[4];

        public static void Ensure()
        {
            if (instance != null) return;
            var host = new GameObject("ChillPatcher_RightIconsCompactLayout");
            Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<RightIconsCompactLayout>();
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < nextUpdate) return;
            nextUpdate = Time.unscaledTime + 0.15f;
            if (root == null)
            {
                var go = GameObject.Find(Path);
                if (go == null) go = GameObject.Find(LegacyPath);
                if (go == null) return;
                root = go.GetComponent<RectTransform>();
                scales.Clear();
            }
            if (root == null || !root.gameObject.activeInHierarchy) return;
            var parent = root.parent as RectTransform;
            if (parent == null || parent.rect.height <= 0) return;
            Arrange(parent);
        }

        private void Arrange(RectTransform parent)
        {
            var group = root.GetComponent<LayoutGroup>();
            if (group != null) group.enabled = false;
            var fitter = root.GetComponent<ContentSizeFitter>();
            if (fitter != null) fitter.enabled = false;
            items.Clear();
            float height = 0f, width = 0f;
            for (int i = 0; i < root.childCount; i++)
            {
                var item = root.GetChild(i) as RectTransform;
                if (item == null || !item.gameObject.activeSelf) continue;
                if (RightBottomRowLayout.IsManaged(item)) continue;
                var ignored = item.GetComponent<LayoutElement>();
                if (ignored != null && ignored.ignoreLayout) continue;
                if (!scales.ContainsKey(item)) scales[item] = item.localScale;
                items.Add(item);
                Vector3 original = scales[item];
                height += Mathf.Max(1f, item.rect.height * Mathf.Abs(original.y));
                width = Mathf.Max(width, item.rect.width * Mathf.Abs(original.x));
            }
            if (items.Count == 0) return;
            const float gap = 8f;
            height += gap * (items.Count - 1);
            float top = parent.rect.yMax;
            float bottom = parent.rect.yMin;
            // Intersect the parent with the canvas so oversize parents cannot hide icons.
            var canvas = root.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                var viewport = canvas.rootCanvas.transform as RectTransform;
                if (viewport != null)
                {
                    viewport.GetWorldCorners(corners);
                    bottom = Mathf.Max(bottom, parent.InverseTransformPoint(corners[0]).y);
                    top = Mathf.Min(top, parent.InverseTransformPoint(corners[1]).y);
                }
            }
            float available = Mathf.Max(1f, top - bottom);
            // Reserve the entire lower utility stack (visibility/settings/exit),
            // which may live outside MostFrontArea rather than beside RightIcons.
            bottom += Mathf.Min(240f, available * 0.35f);
            // Keep clear of the lower control row, even when its height changes.
            foreach (var button in (canvas != null ? canvas.rootCanvas.transform : parent).GetComponentsInChildren<Button>(false))
            {
                if (button.transform.IsChildOf(root)) continue;
                string name = button.name.ToLowerInvariant();
                if (!name.Contains("exit") && !name.Contains("setting") &&
                    !name.Contains("eye")) continue;
                var rect = button.transform as RectTransform;
                if (rect == null) continue;
                rect.GetWorldCorners(corners);
                float edge = parent.InverseTransformPoint(corners[1]).y;
                if (edge < bottom + available * 0.25f)
                    bottom = Mathf.Max(bottom, edge + 12f);
            }
            top -= 12f;
            float scale = Mathf.Min(0.7f, Mathf.Max(0.001f, top - bottom) / height);
            scale = Mathf.Min(scale, Mathf.Max(1f, parent.rect.width - 24f) /
                Mathf.Max(1f, width));
            root.anchorMin = root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(1f, 1f);
            root.localScale = new Vector3(scale, scale, 1f);
            root.sizeDelta = new Vector2(width, height);
            root.anchoredPosition = new Vector2(-12f, top - parent.rect.yMax);
            float y = 0f;
            foreach (var item in items)
            {
                item.anchorMin = item.anchorMax = new Vector2(0.5f, 1f);
                item.pivot = new Vector2(0.5f, 1f);
                item.localScale = scales[item];
                item.anchoredPosition = new Vector2(0f, -y);
                y += Mathf.Max(1f, item.rect.height * Mathf.Abs(scales[item].y)) + gap;
            }
        }
    }
}
