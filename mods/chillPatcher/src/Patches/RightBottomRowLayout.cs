using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    // Lays the three bottom-right utility icons (visibility / settings / exit call)
    // out in a single horizontal row so they can never overlap.
    public sealed class RightBottomRowLayout : MonoBehaviour
    {
        private static RightBottomRowLayout instance;
        private static Canvas cachedCanvas;
        private const string RightIconsPath = "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons";
        private static readonly string[] RowOrder =
        {
            "IconHide_Button", "IconSetting_Button", "IconExit_Button"
        };
        private readonly HashSet<Transform> managed = new HashSet<Transform>();
        private readonly List<RectTransform> row = new List<RectTransform>();
        private readonly Vector3[] corners = new Vector3[4];
        private float nextUpdate;
        private bool reported;

        public static void Ensure()
        {
            if (instance != null) return;
            var host = new GameObject("ChillPatcher_RightBottomRowLayout");
            Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<RightBottomRowLayout>();
        }

        public static bool IsManaged(Transform t)
        {
            return instance != null && t != null && instance.managed.Contains(t);
        }

        private void LateUpdate()
        {
            Step();
        }

        // Also driven from the FacilityMusic patch: a plugin-created GameObject
        // does not survive every scene transition.
        internal static void Tick()
        {
            Ensure();
            if (instance == null) return;
            instance.Step();
        }

        private void Step()
        {
            if (Time.unscaledTime < nextUpdate) return;
            nextUpdate = Time.unscaledTime + 0.25f;
            Arrange();
        }

        private void Arrange()
        {
            var canvas = FindCanvas();
            if (canvas == null) return;
            var canvasRect = canvas.transform as RectTransform;
            if (canvasRect == null) return;
            canvasRect.GetWorldCorners(corners);
            float bottom = corners[0].y;
            float right = corners[2].x;
            if (right <= 0f) return;
            Collect();
            if (row.Count == 0) return;
            float iconW = 1f;
            foreach (var r in row)
                iconW = Mathf.Max(iconW, r.rect.width * Mathf.Abs(r.lossyScale.x));
            float step = iconW * 1.2f;
            int n = row.Count;
            float y = bottom + iconW;
            float startX = right - iconW * 0.85f - step * (n - 1);
            for (int i = 0; i < n; i++)
            {
                var p = row[i].position;
                row[i].position = new Vector3(startX + step * i, y, p.z);
            }
            if (!reported)
            {
                reported = true;
                Plugin.Log.LogInfo("[RightBottomRow] arranged " + n + " icons at y=" + y +
                    " from x=" + startX);
            }
        }

        private void Collect()
        {
            row.Clear();
            managed.Clear();
            var rightIcons = GameObject.Find(RightIconsPath);
            if (rightIcons == null) return;
            var root = rightIcons.transform;
            for (int i = 0; i < RowOrder.Length; i++)
            {
                var t = root.Find(RowOrder[i]);
                if (t == null) continue;
                var rect = t as RectTransform;
                if (rect == null || !rect.gameObject.activeSelf) continue;
                row.Add(rect);
                managed.Add(rect);
            }
        }

        private static Canvas FindCanvas()
        {
            if (cachedCanvas != null) return cachedCanvas;
            var direct = GameObject.Find("Paremt/PCPlatform/Canvas");
            if (direct != null)
            {
                var dc = direct.GetComponent<Canvas>();
                if (dc != null)
                {
                    cachedCanvas = dc.rootCanvas;
                    return cachedCanvas;
                }
            }
            var all = Resources.FindObjectsOfTypeAll<Canvas>();
            Canvas best = null;
            float bestArea = -1f;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.isActiveAndEnabled || !c.gameObject.activeInHierarchy) continue;
                if (c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                if (HasChildNamed(c.transform, "MostFrontArea"))
                {
                    cachedCanvas = c.rootCanvas;
                    return cachedCanvas;
                }
                var rt = c.transform as RectTransform;
                if (rt == null) continue;
                float area = Mathf.Abs(rt.rect.width * rt.rect.height);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = c.rootCanvas;
                }
            }
            cachedCanvas = best;
            return cachedCanvas;
        }

        private static bool HasChildNamed(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name == name) return true;
                if (HasChildNamed(c, name)) return true;
            }
            return false;
        }
    }
}
