using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ChillPatcher.Patches
{
    /// <summary>
    /// 壁纸模式下游戏窗口没有焦点，原生 UGUI 输入框（TMP_InputField）收不到真实按键，
    /// 回车由钩子转发到这里：换行用 TMP 自己的 KeyPressed 走一遍（带校验、光标、OnValueChanged），
    /// 提交/确认则走 OnSubmit，语义与原生输入一致。
    /// </summary>
    internal static class NativeInputReturnHandler
    {
        internal static bool TryDispatch(bool shift)
        {
            var tmp = FindFocused();
            if (tmp == null) return false;

            try
            {
                bool multiLineNewline = tmp.lineType == TMP_InputField.LineType.MultiLineNewline;
                bool singleLine = tmp.lineType == TMP_InputField.LineType.SingleLine;

                if (shift || multiLineNewline)
                {
                    // 单行输入框不插入换行，和原生行为一致（Shift+回车在单行框里无效）
                    if (singleLine) return true;

                    var evt = new Event
                    {
                        type = EventType.KeyDown,
                        keyCode = KeyCode.Return,
                        character = '\n',
                        shift = shift,
                    };
                    tmp.KeyPressed(evt);
                    tmp.ForceLabelUpdate();
                    return true;
                }

                // 单行 / MultiLineSubmit 的普通回车 = 提交
                tmp.OnSubmit(new BaseEventData(EventSystem.current));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[NativeReturn] dispatch failed: {ex.Message}");
                return false;
            }
        }

        private static TMP_InputField FindFocused()
        {
            try
            {
                var es = EventSystem.current;
                var go = es != null ? es.currentSelectedGameObject : null;
                if (go == null) return null;

                var tmp = go.GetComponent<TMP_InputField>();
                if (tmp == null) tmp = go.GetComponentInParent<TMP_InputField>();
                if (tmp == null || !tmp.isActiveAndEnabled || !tmp.isFocused) return null;
                return tmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
