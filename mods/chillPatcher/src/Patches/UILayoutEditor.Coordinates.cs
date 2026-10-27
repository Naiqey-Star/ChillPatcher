using System.Globalization;
using UnityEngine;

namespace ChillPatcher.Patches
{
    public sealed partial class UILayoutEditor
    {
        int coordinateCapture;
        bool coordinateDragging;
        Vector2 coordinateStart;
        UnityEngine.UI.Button coordinateTarget;
        float coordinateValue;
        Vector2 coordinateLast;

        void ReleaseCoordinateCapture()
        {
            if (coordinateCapture != 0 && GUIUtility.hotControl == coordinateCapture)
                GUIUtility.hotControl = 0;
            coordinateCapture = 0;
            coordinateDragging = false;
        }

        void ApplyCoordinateText(string text, bool horizontal)
        {
            float value;
            if (!selected || !float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) || !Finite(value)) return;
            var rect = selected.transform as RectTransform;
            if (!rect) return;
            var p = rect.anchoredPosition;
            MoveSelected(horizontal ? value : p.x, horizontal ? p.y : value, true, true);
        }

        void DrawCoordinateField(string axis, ref string text, bool horizontal)
        {
            GUILayout.Label(axis, GUILayout.Width(18));
            var rect = GUILayoutUtility.GetRect(new GUIContent(text), GUI.skin.textField,
                GUILayout.MinWidth(65), GUILayout.ExpandWidth(true));
            int id = GUIUtility.GetControlID(FocusType.Passive, rect);
            string name = "LayoutCoordinate" + axis;
            var e = Event.current;
            if (coordinateCapture == id && (!selected || selected != coordinateTarget
                || GUIUtility.hotControl != id)) ReleaseCoordinateCapture();
            if (GUI.enabled && e.type == EventType.MouseDown && e.button == 0
                && rect.Contains(e.mousePosition))
            {
                ReleaseCoordinateCapture();
                var targetRect = selected.transform as RectTransform;
                if (targetRect)
                {
                    coordinateCapture = id;
                    coordinateTarget = selected;
                    coordinateStart = coordinateLast = e.mousePosition;
                    coordinateValue = horizontal ? targetRect.anchoredPosition.x : targetRect.anchoredPosition.y;
                    GUIUtility.hotControl = id;
                    e.Use();
                }
            }
            HandleCoordinateDrag(id, name, horizontal, ref text);
            GUI.SetNextControlName(name);
            string next = GUI.TextField(rect, text);
            if (next != text)
            {
                text = next;
                ApplyCoordinateText(text, horizontal);
            }
        }

        void HandleCoordinateDrag(int id, string name, bool horizontal, ref string text)
        {
            if (coordinateCapture != id) return;
            var e = Event.current;
            if (e.type == EventType.MouseDrag)
            {
                if (!coordinateDragging && Mathf.Abs(e.mousePosition.x - coordinateStart.x) >= 3f)
                {
                    coordinateDragging = true;
                    GUI.FocusControl(null);
                }
                if (coordinateDragging)
                {
                    float speed = e.alt ? 0.1f : (e.shift ? 10f : 1f);
                    float next = coordinateValue + (e.mousePosition.x - coordinateLast.x) * speed;
                    if (Finite(next))
                    {
                        coordinateValue = next;
                        text = next.ToString("R", CultureInfo.InvariantCulture);
                        ApplyCoordinateText(text, horizontal);
                    }
                    coordinateLast = e.mousePosition;
                }
                e.Use();
            }
            else if (e.rawType == EventType.MouseUp && e.button == 0)
            {
                bool focus = !coordinateDragging;
                ReleaseCoordinateCapture();
                if (focus) GUI.FocusControl(name);
                e.Use();
            }
            else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                ReleaseCoordinateCapture();
                e.Use();
            }
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused) ReleaseCoordinateCapture();
        }
    }
}
