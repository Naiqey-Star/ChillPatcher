using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
namespace ChillPatcher.Patches
{
    public sealed partial class UILayoutEditor
    {
        void Capture(RectTransform r)
        {
            if (originals.ContainsKey(r)) return;
            var e = r.GetComponent<LayoutElement>();
            var o = new Original { position = r.anchoredPosition, element = e, added = !e, ignored = e && e.ignoreLayout };
            if (!e) o.element = r.gameObject.AddComponent<LayoutElement>();
            originals.Add(r, o);
        }
        void RestoreOriginals()
        {
            foreach (var pair in originals)
            {
                if (!pair.Key) continue;
                pair.Key.anchoredPosition = pair.Value.position;
                var e = pair.Value.element;
                if (e) e.ignoreLayout = pair.Value.ignored;
            }
            originals.Clear();
        }
        void ValidateAndLoad(LayoutData data)
        {
            if (data == null || data.version != 1 || data.entries == null) throw new InvalidOperationException("Invalid layout data");
            var validated = new System.Collections.Generic.Dictionary<string, Vector2>();
            foreach (var e in data.entries)
            {
                if (e == null || string.IsNullOrEmpty(e.key) || !Finite(e.x) || !Finite(e.y)) throw new InvalidOperationException("Invalid layout entry");
                validated[e.key] = new Vector2(e.x, e.y);
            }
            RestoreOriginals(); positions.Clear();
            foreach (var p in validated) positions.Add(p.Key, p.Value);
            ApplyPositions(); SetStatus("Saved layout loaded", "已加载保存的布局");
        }
        void CreateShield()
        {
            shield = new GameObject("ChillPatcher.LayoutShield", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            DontDestroyOnLoad(shield);
            var canvas = shield.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var panel = new GameObject("BlockClicks", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panel.transform.SetParent(shield.transform, false);
            var r = panel.GetComponent<RectTransform>();
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
            var image = panel.GetComponent<Image>();
            image.color = new Color(0, 0, 0, 0.03f); image.raycastTarget = true;
        }
        void OnEnable() { Canvas.willRenderCanvases += ApplyPositions; }
        void OnDisable()
        {
            ReleaseCoordinateCapture();
            Canvas.willRenderCanvases -= ApplyPositions;
            editing = false;
            if (shield) shield.SetActive(false);
            RestoreOriginals();
        }
        void OnDestroy()
        {
            Canvas.willRenderCanvases -= ApplyPositions;
            RestoreOriginals();
            if (shield) Destroy(shield);
            ReleaseLocalization();
            if (instance == this) instance = null;
        }
        void OnGUI()
        {
            if (!editing)
            {
                ReleaseCoordinateCapture();
                // 编辑器关闭时不绘制入口；使用 F8 直接切换编辑面板。
                return;
            }
            window.width = Mathf.Min(510, Screen.width);
            window.height = Mathf.Min(560, Screen.height);
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            var previousSkin = GUI.skin;
            try
            {
                GUI.skin = LocalizedSkin();
                window = GUILayout.Window(GetInstanceID(), window, DrawWindow, T("UI Layout Editor", "界面布局编辑器"));
            }
            finally { GUI.skin = previousSkin; }
        }
        GUIStyle listStyle;
        bool listDirty = true;
        string listFilter;
        int listPage;
        const int PageSize = 30;
        sealed class EditorRow
        {
            public Button button;
            public GUIContent normal, selected;
        }
        readonly System.Collections.Generic.List<EditorRow> editorRows = new System.Collections.Generic.List<EditorRow>();
        void InvalidateEditorList() { listDirty = true; }
        void RefreshEditorList()
        {
            if (!listDirty && listFilter == filter) return;
            editorRows.Clear();
            foreach (var b in buttons)
            {
                if (!b) continue;
                string key;
                if (!buttonKeys.TryGetValue(b, out key)) continue;
                if (!MatchesElementFilter(b, key, filter)) continue;
                string caption = ElementCaption(b);
                editorRows.Add(new EditorRow { button = b, normal = new GUIContent(caption, key), selected = new GUIContent("> " + caption, key) });
            }
            if (listFilter != filter) listPage = 0;
            listFilter = filter;
            listDirty = false;
            listPage = Mathf.Clamp(listPage, 0, Math.Max(0, (editorRows.Count - 1) / PageSize));
        }
        void DrawWindow(int id)
        {
            GUILayout.Label(T("Select overlapping buttons from the list. X right / Y up.", "从列表选择重叠按钮。X 向右 / Y 向上。"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("Save", "保存"))) Save();
            if (GUILayout.Button(T("Load saved", "加载存档"))) Load();
            if (GUILayout.Button(T("Close", "关闭"))) SetEditing(false);
            if (GUILayout.Button(T("简体中文", "English"))) ToggleLanguage();
            GUILayout.EndHorizontal();
            filter = GUILayout.TextField(filter);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Max(50, window.height - 300)));
            RefreshEditorList();
            if (listStyle == null) listStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            int end = Math.Min(editorRows.Count, (listPage + 1) * PageSize);
            for (int i = listPage * PageSize; i < end; i++)
            {
                var row = editorRows[i];
                if (!row.button) continue;
                if (GUILayout.Button(selected == row.button ? row.selected : row.normal, listStyle, GUILayout.Width(Mathf.Max(100, window.width - 44)))) SelectButton(row.button);
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("Previous", "上一页")) && listPage > 0) { listPage--; scroll = Vector2.zero; }
            GUILayout.Label((listPage + 1) + " / " + Math.Max(1, (editorRows.Count + PageSize - 1) / PageSize));
            if (GUILayout.Button(T("Next", "下一页")) && (listPage + 1) * PageSize < editorRows.Count) { listPage++; scroll = Vector2.zero; }
            GUILayout.EndHorizontal();
            DrawPositionControls();
            GUILayout.Label(LocalizedStatus);
            GUI.DragWindow(new Rect(0, 0, window.width, 22));
        }
        void SelectButton(Button b)
        {
            selected = b;
            var r = b.transform as RectTransform;
            if (!r) return;
            x = r.anchoredPosition.x.ToString(CultureInfo.InvariantCulture);
            y = r.anchoredPosition.y.ToString(CultureInfo.InvariantCulture);
        }
        void MoveSelected(float dx, float dy, bool absolute, bool preserveInput = false)
        {
            if (!selected) return;
            var r = selected.transform as RectTransform;
            if (!r || !Finite(dx) || !Finite(dy)) return;
            Vector2 p = absolute ? new Vector2(dx, dy) : r.anchoredPosition + new Vector2(dx, dy);
            if (!Finite(p.x) || !Finite(p.y)) return;
            Capture(r);
            positions[Key(selected.transform)] = p;
            ApplyPositions();
            if (!preserveInput) SelectButton(selected);
        }
        void ResetSelected()
        {
            if (!selected) return;
            positions.Remove(Key(selected.transform));
            var r = selected.transform as RectTransform;
            Original o;
            if (r && originals.TryGetValue(r, out o))
            {
                r.anchoredPosition = o.position;
                // Retain the neutral component so same-frame Capture cannot reuse an object queued for destruction.
                if (o.element) o.element.ignoreLayout = o.ignored;
                originals.Remove(r);
            }
            SelectButton(selected);
            SetStatus("Selected reset; Save to persist.", "已重置所选项；点击保存以持久化。");
        }
        void DrawPositionControls()
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && selected;
            GUILayout.BeginHorizontal();
            DrawCoordinateField("X", ref x, true);
            DrawCoordinateField("Y", ref y, false);
            GUILayout.EndHorizontal();
            float step = Event.current.shift ? 10f : 1f;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("Left", "左"))) MoveSelected(-step, 0, false);
            if (GUILayout.Button(T("Right", "右"))) MoveSelected(step, 0, false);
            if (GUILayout.Button(T("Up", "上"))) MoveSelected(0, step, false);
            if (GUILayout.Button(T("Down", "下"))) MoveSelected(0, -step, false);
            GUILayout.EndHorizontal();
            if (GUILayout.Button(T("Reset selected", "重置所选项"))) ResetSelected();
            GUI.enabled = wasEnabled;
            if (GUILayout.Button(T("Reset all", "重置全部")))
            {
                positions.Clear();
                RestoreOriginals();
                if (selected) SelectButton(selected);
                SetStatus("All positions reset; Save to persist.", "已重置全部位置；点击保存以持久化。");
            }
            GUILayout.Label(T("Step: 1 (Shift: 10). Save persists changes and resets.", "步长：1（Shift：10）。修改或重置后，点击保存以持久化。"));
        }
    }
}
