using System.Runtime.InteropServices;

namespace HarpAutoPlayer.Input;

/// <summary>
/// 通过 Windows SendInput 模拟键盘与鼠标（真实全局输入，焦点在游戏窗口即可生效）。
/// </summary>
public static class InputSender
{
    public enum MouseButton { Left, Right, Middle }

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const ushort VK_OEM_COMMA = 0xBC;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>当前前台窗口句柄（仅本程序目标系统有效）。</summary>
    public static IntPtr ForegroundWindow =>
        OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;

    /// <summary>把指定窗口置前（用于停止时把焦点还给游戏后再补发一次“松开”）。</summary>
    public static void BringToForeground(IntPtr hWnd)
    {
        if (OperatingSystem.IsWindows() && hWnd != IntPtr.Zero) SetForegroundWindow(hWnd);
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>可模拟的键：Z..M 及键盘逗号“,”（高高音do）。</summary>
    private static ushort VkCodeOf(char c)
    {
        if (c is >= 'A' and <= 'Z') return (ushort)c;
        if (c == ',') return VK_OEM_COMMA;
        return 0;
    }

    private static void SendKey(bool down, char vkChar)
    {
        if (!OperatingSystem.IsWindows()) return;   // 该功能仅本程序目标系统有效
        ushort vk = VkCodeOf(char.ToUpperInvariant(vkChar));
        if (vk == 0) return;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC),
                    dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouse(MouseButton button, bool down)
    {
        if (!OperatingSystem.IsWindows()) return;   // 见上
        uint flag = button switch
        {
            MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void KeyDown(char c) => SendKey(true, c);
    public static void KeyUp(char c) => SendKey(false, c);

    public static void MouseDown(MouseButton b) => SendMouse(b, true);
    public static void MouseUp(MouseButton b) => SendMouse(b, false);

    /// <summary>把所有键/鼠标键抬起，用于停止/暂停时清理状态。</summary>
    public static void ReleaseEverything()
    {
        if (!OperatingSystem.IsWindows()) return;   // 见上
        foreach (char c in "ZXCVBNM,") KeyUp(c);
        MouseUp(MouseButton.Left);
        MouseUp(MouseButton.Right);
        MouseUp(MouseButton.Middle);
    }
}
