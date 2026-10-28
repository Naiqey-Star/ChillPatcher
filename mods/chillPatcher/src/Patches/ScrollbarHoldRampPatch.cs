using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    /// <summary>
    /// uGUI 的 Scrollbar 在按住滑轨空白处时，每帧固定跳一个手柄长度的值，速度偏快。
    /// 这里接管 ClickRepeat 协程：第一下点击仍然立即跳一步（保留单击手感），
    /// 之后的连续滚动从 0 线性加速，达到 ScrollHoldRampSeconds 后维持原速。
    /// </summary>
    [HarmonyPatch(typeof(Scrollbar), "ClickRepeat", new[] { typeof(PointerEventData) })]
    internal static class ScrollbarHoldRampPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Scrollbar __instance, PointerEventData eventData, ref IEnumerator __result)
        {
            if (__instance == null || eventData == null) return true;
            if (PluginConfig.ScrollHoldRampEnabled == null || !PluginConfig.ScrollHoldRampEnabled.Value) return true;
            if (__instance.numberOfSteps > 0) return true; // 步进模式的滚动条不接管

            __result = RampClickRepeat(__instance, eventData);
            return false;
        }

        private static IEnumerator RampClickRepeat(Scrollbar bar, PointerEventData eventData)
        {
            float rampSeconds = Mathf.Max(0.05f, PluginConfig.ScrollHoldRampSeconds == null
                ? 3f
                : PluginConfig.ScrollHoldRampSeconds.Value);
            float startTime = Time.unscaledTime;
            bool firstStep = true;

            while (true)
            {
                if (bar == null || bar.handleRect == null) yield break;
                if (eventData == null) yield break;
                if (!Input.GetMouseButton(0) && eventData.pointerPress == null) yield break;

                Camera cam = eventData.pressEventCamera;
                Vector2 screenPosition = eventData.position;

                // 指针移到手柄上就交还给组件自己处理
                if (RectTransformUtility.RectangleContainsScreenPoint(bar.handleRect, screenPosition, cam))
                    yield break;

                Vector2 localPoint;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(bar.handleRect, screenPosition, cam, out localPoint))
                    yield break;

                bool horizontal = bar.direction == Scrollbar.Direction.LeftToRight
                                  || bar.direction == Scrollbar.Direction.RightToLeft;
                float coordinate = horizontal ? localPoint.x : localPoint.y;
                float stepSize = coordinate < 0f ? -bar.size : bar.size;

                float elapsed = Time.unscaledTime - startTime;
                float factor = firstStep ? 1f : Mathf.Clamp01(elapsed / rampSeconds);
                firstStep = false;

                if (stepSize > 0f)
                {
                    bool reverse = bar.direction == Scrollbar.Direction.RightToLeft
                                   || bar.direction == Scrollbar.Direction.TopToBottom;
                    float next = bar.value + (reverse ? stepSize : -stepSize) * factor;
                    next = Mathf.Clamp01(next);
                    next = Mathf.Round(next * 10000f) / 10000f;
                    bar.value = next;
                }

                yield return new WaitForEndOfFrame();
            }
        }
    }
}
