using System.Runtime.InteropServices;
using GameInstrumentPlayer.Profiles;

namespace GameInstrumentPlayer.Input;

/// <summary>
/// 输出层：用 Windows SendInput 把方案里的动作变成真实的键鼠输入。
/// 支持两种发送方式（方案里可选）：键盘扫描码（多数游戏只认这个）与虚拟键码（少数游戏）。
/// </summary>
public static class InputSender
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;

    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    private const uint MAPVK_VK_TO_VSC_EX = 4;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static IntPtr ForegroundWindow =>
        OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;

    /// <summary>前台窗口标题，用于诊断按键发给了谁。</summary>
    public static string ForegroundWindowTitle
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "";
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            var sb = new System.Text.StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    /// <summary>置前后台窗口；停止时先把焦点还给游戏，再补发一次「松开」。</summary>
    public static void BringToForeground(IntPtr hWnd)
    {
        if (OperatingSystem.IsWindows() && hWnd != IntPtr.Zero) SetForegroundWindow(hWnd);
    }

    // ================= 按键名 → 虚拟键码 =================

    // ================= 当前按下的东西 =================

    private static readonly object Gate = new();
    private static readonly HashSet<int> HeldKeys = new();    // 虚拟键码
    private static readonly HashSet<int> HeldMouse = new();   // 1=左 2=中 3=右 4=侧1 5=侧2

    // ================= 发送 =================

    /// <summary>发送一个动作。vkMode = true 时发虚拟键码，否则发扫描码。</summary>
    public static void Send(NoteAction action, bool down, bool vkMode = false)
    {
        if (!OperatingSystem.IsWindows() || action.IsNone) return;

        if (action.Kind == ActionKind.Mouse)
        {
            SendMouse(action, down);
            return;
        }

        // 动作里的键可能是单字符（Z），也可能是命名键（LeftShift）
        string keyName = ActionCodec.KeyNameOf(action.Code);
        ushort vk = keyName.Length == 0 ? (ushort)0 : ActionCodec.VirtualKeyOf(keyName);
        if (vk == 0 && action.Code is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            vk = (ushort)action.Code;
        if (vk == 0) return;
        SendKey(vk, down, vkMode);
    }

    private static void SendKey(ushort vk, bool down, bool vkMode)
    {
        ushort scan = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC_EX);
        bool extended = (scan & 0xE000) != 0 ||
                        vk is 0xA3 or 0xA5 or 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22;
        scan &= 0x00FF;

        uint flags = 0;
        if (!down) flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        // 默认发扫描码：很多游戏与 DirectInput 只认扫描码
        if (!vkMode) flags |= KEYEVENTF_SCANCODE;

        var ki = new KEYBDINPUT
        {
            wVk = vkMode ? vk : (ushort)0,
            wScan = scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        var input = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = ki } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

        lock (Gate)
        {
            if (down) HeldKeys.Add(vk); else HeldKeys.Remove(vk);
        }
    }

    private static void SendMouse(NoteAction action, bool down)
    {
        int id = action.Code;   // 1=左 2=中 3=右 4=侧1 5=侧2
        uint flag = id switch
        {
            1 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            3 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP
        };
        uint data = id switch
        {
            4 => XBUTTON1,
            5 => XBUTTON2,
            _ => 0
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0, dy = 0, mouseData = data,
                    dwFlags = flag, time = 0, dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

        lock (Gate)
        {
            if (down) HeldMouse.Add(id); else HeldMouse.Remove(id);
        }
    }

    /// <summary>把所有还按着的东西抬起来，用于停止/暂停时清理状态。</summary>
    public static void ReleaseEverything()
    {
        if (!OperatingSystem.IsWindows()) return;

        List<int> keys;
        List<int> mice;
        lock (Gate)
        {
            keys = HeldKeys.ToList();
            mice = HeldMouse.ToList();
            HeldKeys.Clear();
            HeldMouse.Clear();
        }

        foreach (int vk in keys) SendKey((ushort)vk, down: false, vkMode: false);
        foreach (int id in mice) SendMouse(new NoteAction(ActionKind.Mouse, id), down: false);
    }
}
