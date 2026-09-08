using System.Runtime.InteropServices;
using System.Diagnostics;

namespace HarpAutoPlayer.Input;

/// <summary>
/// 全局热键（游戏中也能触发）：Windows WH_KEYBOARD_LL 低层键盘钩子，独立消息线程。
/// 回调在工作线程上触发，UI 需自行 marshal。
/// </summary>
public static class GlobalHotkeys
{
    public static event Action<int, bool>? KeyState;   // (虚拟键码, 是否按下) 工作线程
    public static event Action<string>? Status;        // 注册状态/错误（工作线程）

    private static readonly object _sync = new();
    private static readonly HashSet<int> _active = new();
    private static Thread? _thread;
    private static volatile bool _running;
    private static readonly Dictionary<int, long> _lastDownTick = new();
    private static uint _winThreadId;

    public static bool IsAvailable => OperatingSystem.IsWindows();
    public static bool Running => _running;

    /// <summary>功能键虚拟码（F1..F12）。</summary>
    public static int FunctionKeyCode(int n)
    {
        if (n is < 1 or > 12) return 0;
        return 0x70 + n - 1;   // VK_F1..VK_F12
    }

    public static void SetActive(IEnumerable<int> codes)
    {
        lock (_sync)
        {
            _active.Clear();
            foreach (var c in codes)
                if (c != 0) _active.Add(c);
        }
    }

    public static bool Start()
    {
        if (_running || !IsAvailable) return _running;
        _running = true;
        _thread = new Thread(Worker) { IsBackground = true, Name = "GlobalHotkeys" };
        _thread.Start();
        return true;
    }

    public static void Stop()
    {
        _running = false;
        // 唤醒钩子线程阻塞的 GetMessage 消息循环
        if (_winThreadId != 0)
            PostThreadMessageW(_winThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }

    // ================= 事件分发（工作线程） =================
    private static void RaiseKey(int code, bool down)
    {
        if (!down) return;   // 只用按下触发（避免重复/抬起抖动）

        lock (_sync)
        {
            if (!_active.Contains(code)) return;
            long now = Environment.TickCount64;
            if (_lastDownTick.TryGetValue(code, out long last) && now - last < 100) return; // 防连发
            _lastDownTick[code] = now;
        }
        KeyState?.Invoke(code, true);
    }

    // ================= 低层键盘钩子 =================
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static readonly LowLevelKeyboardProc WinProc = WinHookCallback;
    private static IntPtr _winHook;

    private static IntPtr WinHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (uint)wParam == WM_KEYDOWN)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((kbd.flags & LLKHF_INJECTED) == 0)
                RaiseKey((int)kbd.vkCode, true);
        }
        return CallNextHookEx(_winHook, nCode, wParam, lParam);
    }

    private static void Worker()
    {
        try
        {
            using var cur = Process.GetCurrentProcess();
            IntPtr mod = cur.MainModule is { } m ? GetModuleHandleW(m.ModuleName) : IntPtr.Zero;
            _winHook = SetWindowsHookExW(WH_KEYBOARD_LL, WinProc, mod, 0);
            if (_winHook == IntPtr.Zero)
            {
                _running = false;
                Status?.Invoke("全局热键注册失败（系统钩子不可用）");
                return;
            }

            _winThreadId = (uint)Environment.CurrentManagedThreadId; // 钩子消息线程即当前线程
            Status?.Invoke("全局热键已启用（游戏中直接生效）");

            while (_running && GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
            {
                if (!_running) break;
            }

            UnhookWindowsHookEx(_winHook);
            _winHook = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            _running = false;
            Status?.Invoke($"全局热键异常：{ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }
}
