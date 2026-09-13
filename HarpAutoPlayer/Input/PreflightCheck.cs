using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HarpAutoPlayer.Input;

/// <summary>
/// 播放前自检：只查真正影响“按键能否送达”的两项 —— 管理员权限、前台窗口输入法。
/// </summary>
public static class PreflightCheck
{
    public sealed record Check(string Name, bool Passed, string Detail);

    public sealed class Report
    {
        public Check Admin { get; init; } = new("管理员", false, "");
        public Check Ime { get; init; } = new("输入法", false, "");
        public bool AllPassed => Admin.Passed && Ime.Passed;
    }

    /// <summary>输入法状态检测结果。</summary>
    public enum ImeMode
    {
        /// <summary>不是中日韩布局，按键直达游戏。</summary>
        NotCjk,
        /// <summary>中日韩布局，但输入法处于英文/半角模式，按键直达游戏。</summary>
        English,
        /// <summary>输入法打开且处于中文/假名输入状态，会截走按键。</summary>
        Native,
        /// <summary>读不到输入法状态。</summary>
        Unknown
    }

    // ---------------- P/Invoke ----------------
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetOpenStatus(IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint conversion, out uint sentence);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    // 输入法状态：WM_IME_CONTROL / IMC_GETOPENSTATUS 可以跨进程查询，最可靠
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK = 0x0001;
    /// <summary>IME_CMODE_ALPHANUMERIC：输入法开着，但处于英文/半角模式。</summary>
    private const uint IME_CMODE_ALPHANUMERIC = 0x0000;

    public static bool IsSelfElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY, out token))
                return false;
            if (!GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _))
                return false;
            return elevated != 0;
        }
        catch { return false; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    /// <summary>
    /// 前台窗口所属线程的键盘布局；低 16 位是 LANGID（0x0804 简中、0x0404 繁中、0x0411 日文）。
    /// 注意：它只说明"装了哪种键盘布局"，不说明输入法现在是中文还是英文。
    /// </summary>
    public static ushort ForegroundKeyboardLayoutId()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        uint tid = GetWindowThreadProcessId(h, out _);
        IntPtr hkl = GetKeyboardLayout(tid == 0 ? 0 : tid);
        return (ushort)(hkl.ToInt64() & 0xFFFF);
    }

    /// <summary>这个 LANGID 是否属于中日韩（需要看输入法开关状态）的语言。</summary>
    public static bool IsCjkLayout(ushort lang) =>
        lang is 0x0804 or 0x0404 or 0x0411 or 0x0412;   // 简中 / 繁中 / 日文 / 韩文

    /// <summary>LANGID 的中文名（写日志用）。</summary>
    public static string LayoutName(ushort lang) => lang switch
    {
        0x0804 => "简体中文",
        0x0404 => "繁体中文",
        0x0411 => "日文",
        0x0412 => "韩文",
        0x0409 => "英文",
        _ => $"0x{lang:X4}"
    };

    /// <summary>
    /// 纯判定逻辑（便于测试）：给出布局与输入法状态，返回结论。
    /// </summary>
    /// <param name="lang">键盘布局 LANGID，0 = 读不到。</param>
    /// <param name="open">输入法开关：1 开、0 关、-1 读不到。</param>
    /// <param name="alphanumeric">是否英文/半角模式；仅在 open = 1 时有意义。</param>
    public static ImeMode Decide(ushort lang, int open, bool? alphanumeric)
    {
        if (lang == 0) return ImeMode.Unknown;
        if (!IsCjkLayout(lang)) return ImeMode.NotCjk;
        if (open == 0) return ImeMode.English;    // 输入法关着 = 英文直通
        if (open < 0) return ImeMode.Unknown;     // 读不到开关状态
        if (alphanumeric == true) return ImeMode.English;
        if (alphanumeric == false) return ImeMode.Native;
        return ImeMode.Unknown;
    }

    /// <summary>
    /// 读取前台窗口的输入法状态。仅当输入法**打开且处于中文/假名输入状态**时才判定为会截键 ——
    /// 中文布局切到英文模式时按键是直达游戏的，不该拦。
    /// </summary>
    public static ImeMode DetectImeMode()
    {
        if (!OperatingSystem.IsWindows()) return ImeMode.Unknown;
        ushort lang = ForegroundKeyboardLayoutId();
        if (lang == 0 || !IsCjkLayout(lang)) return Decide(lang, 0, null);

        IntPtr h = GetForegroundWindow();
        int open = ReadOpenStatus(h);
        bool? alnum = open == 1 ? ReadAlphanumeric(h) : null;
        return Decide(lang, open, alnum);
    }

    /// <summary>读输入法开关：1 开、0 关、-1 读不到。跨进程用 WM_IME_CONTROL，失败再退回 ImmGetContext。</summary>
    private static int ReadOpenStatus(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
        {
            try
            {
                IntPtr r = SendMessageTimeoutW(hWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, 120, out IntPtr result);
                if (r != IntPtr.Zero) return result != IntPtr.Zero ? 1 : 0;
            }
            catch { /* 目标进程无响应时忽略，退回 ImmGetContext */ }
        }

        IntPtr hImc = IntPtr.Zero;
        try
        {
            hImc = ImmGetContext(hWnd);
            if (hImc == IntPtr.Zero) return -1;
            return ImmGetOpenStatus(hImc) ? 1 : 0;
        }
        catch { return -1; }
        finally { if (hImc != IntPtr.Zero) ImmReleaseContext(hWnd, hImc); }
    }

    /// <summary>输入法是否处于英文/半角模式：true 是、false 否、null 读不到。</summary>
    private static bool? ReadAlphanumeric(IntPtr hWnd)
    {
        IntPtr hImc = IntPtr.Zero;
        try
        {
            hImc = ImmGetContext(hWnd);
            if (hImc == IntPtr.Zero) return null;
            if (!ImmGetConversionStatus(hImc, out uint conversion, out _)) return null;
            return (conversion & IME_CMODE_ALPHANUMERIC) == IME_CMODE_ALPHANUMERIC;
        }
        catch { return null; }
        finally { if (hImc != IntPtr.Zero) ImmReleaseContext(hWnd, hImc); }
    }

    public static Report Run()
    {
        if (!OperatingSystem.IsWindows())
            return new Report
            {
                Admin = new Check("管理员", false, "非 Windows，无法模拟输入"),
                Ime = new Check("输入法", false, "非 Windows")
            };

        // ① 模拟按键受 UIPI 限制，未提权时会被系统直接拦截
        bool admin = IsSelfElevated();
        var adminCheck = new Check("管理员", admin,
            admin ? "已提权" : "未提权，游戏为管理员时按键会被拦截");

        // ② 前台输入法：只有"输入法开着且在中文/假名状态"才会截走按键。
        //    中文键盘布局 + 英文模式是正常可用的，不要误报。
        ushort lang = ForegroundKeyboardLayoutId();
        var imeCheck = DetectImeMode() switch
        {
            ImeMode.NotCjk => new Check("输入法", true, $"{LayoutName(lang)}布局，正常"),
            ImeMode.English => new Check("输入法", true,
                $"{LayoutName(lang)}布局已切英文模式，按键直达"),
            ImeMode.Native => new Check("输入法", false,
                $"{LayoutName(lang)}输入法在中文/假名状态，请按 Shift 或 Ctrl+空格 切英文"),
            _ => new Check("输入法", false, "读不到输入法状态，请确认已切英文")
        };

        return new Report { Admin = adminCheck, Ime = imeCheck };
    }
}
