namespace ChillPatcher.Patches
{
    // 壁纸模式下游戏窗口没有焦点，回车到不了输入框。
    // 钩子只负责“把这次回车转发给当前获焦的 OneJS 输入框”，
    // 发送/换行仍由 AIChat 页面自己的逻辑处理，这里不碰任何聊天状态。
    internal static class AIChatEnterHandler
    {
        internal static void Tick()
        {
            // 前台归属每帧复核一次：切到别的应用时立刻放弃输入
            KeyboardHookPatch.RefreshCaptureState();

            // 同一帧内可能有多条请求，但一次只处理一条，顺序与用户按键一致
            bool shift;
            while (KeyboardHookPatch.TryDequeueReturn(out shift))
            {
                bool ok = shift
                    ? UIToolkitInputDispatcher.InsertNewline()
                    : UIToolkitInputDispatcher.DispatchReturn();

                // AIchat 输入框没获焦时，把这次回车交给游戏原生输入框（日记/笔记等）
                if (!ok) ok = NativeInputReturnHandler.TryDispatch(shift);

                if (!ok)
                {
                    Plugin.Log.LogWarning("[AIChatKeys] no focused text field, enter dropped (shift=" + shift + ")");
                    return;
                }

                // 一次按键只派发一次，剩余请求留到下一帧，避免同一帧连发
                break;
            }
        }
    }
}
