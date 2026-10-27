using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChillPatcher.Patches.Rime
{
    /// <summary>
    /// Windows VK_CODE 到 Rime Keycode 转换
    /// 参考 weasel/WeaselTSF/KeyEvent.cpp
    /// </summary>
    public static class KeyEventConverter
    {
        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(
            uint wVirtKey,
            uint wScanCode,
            byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
            int cchBuff,
            uint wFlags,
            IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        // ibus::Keycode 定义(来自 weasel/include/KeyEvent.h)
        private static readonly bool[] Held = new bool[256];
        private static bool seeded, caps;
        // Re-seed after hook restart; key-up events may have been missed.
        public static void ResetState()
        {
            Array.Clear(Held, 0, Held.Length);
            seeded = false;
            caps = false;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        public static void ObserveKey(uint vk, int message)
        {
            if (vk >= 256) return;
            bool down = message == 0x100 || message == 0x104;
            if (!down && message != 0x101 && message != 0x105) return;
            if (!seeded)
            {
                for (int i = 0; i < 256; i++) Held[i] = (GetAsyncKeyState(i) & 0x8000) != 0;
                caps = (GetKeyState(0x14) & 1) != 0;
                // LL events identify left/right modifiers; do not retain generic seed bits.
                Held[0x10] = Held[0x11] = Held[0x12] = false;
                seeded = true;
            }
            // LL callback runs before the current event updates async state.
            if (vk == 0x14 && down && !Held[vk]) caps = !caps;
            Held[vk] = down;
        }
        /// <summary>
        /// 最近一次观察到的 Shift 是否按下。低级钩子在本事件生效前触发，
        /// GetAsyncKeyState 不可靠，因此用观察到的按键状态判断修饰键。
        /// </summary>
        public static bool ShiftHeld => Held[0xA0] || Held[0xA1] || Held[0x10];
        private static void ApplyModifiers(byte[] state)
        {
            if (!seeded) return;
            for (int i = 0xA0; i <= 0xA5; i++) state[i] = Held[i] ? (byte)128 : (byte)0;
            state[0x10] = (Held[0xA0] || Held[0xA1] || Held[0x10]) ? (byte)128 : (byte)0;
            state[0x11] = (Held[0xA2] || Held[0xA3] || Held[0x11]) ? (byte)128 : (byte)0;
            state[0x12] = (Held[0xA4] || Held[0xA5] || Held[0x12]) ? (byte)128 : (byte)0;
            state[0x14] = caps ? (byte)1 : (byte)0;
        }
        private const int BackSpace = 0xFF08;
        private const int Tab = 0xFF09;
        private const int Return = 0xFF0D;
        private const int Escape = 0xFF1B;
        private const int Delete = 0xFFFF;
        private const int Home = 0xFF50;
        private const int Left = 0xFF51;
        private const int Up = 0xFF52;
        private const int Right = 0xFF53;
        private const int Down = 0xFF54;
        private const int Prior = 0xFF55;  // Page Up
        private const int Next = 0xFF56;   // Page Down
        private const int End = 0xFF57;
        private const int Space = 0x0020;
        private const int KP_Enter = 0xFF8D;

        /// <summary>
        /// 转换 Windows VK_CODE 为 Rime keycode
        /// </summary>
        public static bool ConvertKeyEvent(uint vkCode, uint scanCode, out int keycode, out int mask)
        {
            keycode = 0;
            mask = 0; // ibus::NULL_MASK

            // 获取键盘状态
            byte[] keyState = new byte[256];
            if (!GetKeyboardState(keyState) && !seeded)
            {
                return false;
            }

            // 设置 mask (修饰键状态)
            ApplyModifiers(keyState);
            const byte KEY_DOWN = 0x80;
            const byte TOGGLED = 0x01;

            if ((keyState[0x10] & KEY_DOWN) != 0) // VK_SHIFT
                mask |= 1 << 0; // SHIFT_MASK

            if ((keyState[0x14] & TOGGLED) != 0) // VK_CAPITAL
                mask |= 1 << 1; // LOCK_MASK

            if ((keyState[0x11] & KEY_DOWN) != 0) // VK_CONTROL
                mask |= 1 << 2; // CONTROL_MASK

            if ((keyState[0x12] & KEY_DOWN) != 0) // VK_MENU (Alt)
                mask |= 1 << 3; // ALT_MASK

            // 先尝试特殊键转换
            int specialKey = TranslateKeycode(vkCode);
            if (specialKey != 0)
            {
                keycode = specialKey;
                return true;
            }

            // 普通字符键:使用 ToUnicodeEx 转换
            StringBuilder buffer = new StringBuilder(8);
            byte[] tempKeyState = new byte[256];
            Array.Copy(keyState, tempKeyState, 256);

            // 清除 Ctrl 和 Alt 状态以获取字符
            tempKeyState[0x11] = 0; // VK_CONTROL
            tempKeyState[0x12] = 0; // VK_MENU

            // The hook thread's layout can differ from the active desktop layout.
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            IntPtr hkl = GetKeyboardLayout(foregroundThread);
            if (scanCode == 0) scanCode = MapVirtualKeyEx(vkCode, 0, hkl);
            // Windows 10+: do not mutate the foreground thread's dead-key state.
            int result = ToUnicodeEx(vkCode, scanCode, tempKeyState, buffer, buffer.Capacity, 4, hkl);

            if (result == 1)
            {
                // 成功转换为单个字符
                keycode = buffer[0];
                return true;
            }

            // 无法转换
            return false;
        }

        /// <summary>
        /// 转换特殊键 VK_CODE 为 ibus::Keycode
        /// </summary>
        private static int TranslateKeycode(uint vkCode)
        {
            switch (vkCode)
            {
                case 0x08: return BackSpace; // VK_BACK
                case 0x09: return Tab;       // VK_TAB
                case 0x0D: return Return;    // VK_RETURN
                case 0x1B: return Escape;    // VK_ESCAPE
                case 0x20: return Space;     // VK_SPACE
                case 0x21: return Prior;     // VK_PRIOR (Page Up)
                case 0x22: return Next;      // VK_NEXT (Page Down)
                case 0x23: return End;       // VK_END
                case 0x24: return Home;      // VK_HOME
                case 0x25: return Left;      // VK_LEFT
                case 0x26: return Up;        // VK_UP
                case 0x27: return Right;     // VK_RIGHT
                case 0x28: return Down;      // VK_DOWN
                case 0x2E: return Delete;    // VK_DELETE
                
                // 功能键 F1-F12
                case 0x70: return 0xFFBE; // F1
                case 0x71: return 0xFFBF; // F2
                case 0x72: return 0xFFC0; // F3
                case 0x73: return 0xFFC1; // F4
                case 0x74: return 0xFFC2; // F5
                case 0x75: return 0xFFC3; // F6
                case 0x76: return 0xFFC4; // F7
                case 0x77: return 0xFFC5; // F8
                case 0x78: return 0xFFC6; // F9
                case 0x79: return 0xFFC7; // F10
                case 0x7A: return 0xFFC8; // F11
                case 0x7B: return 0xFFC9; // F12

                default: return 0; // 不是特殊键
            }
        }
    }
}
