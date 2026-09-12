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

    // ---------------- P/Invoke ----------------
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

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

        // ② 前台输入法：中日韩 IME 会截走按键
        ushort lang = ForegroundKeyboardLayoutId();
        Check imeCheck;
        if (lang == 0)
        {
            imeCheck = new Check("输入法", false, "读不到键盘布局，请确认已切英文");
        }
        else if (lang is 0x0804 or 0x0404 or 0x0411 or 0x0412)
        {
            string name = lang switch
            {
                0x0804 => "简体中文",
                0x0404 => "繁体中文",
                0x0411 => "日文",
                _ => "韩文"
            };
            imeCheck = new Check("输入法", false, $"{name}，请切英文");
        }
        else
        {
            imeCheck = new Check("输入法", true, "非 IME 布局，正常");
        }

        return new Report { Admin = adminCheck, Ime = imeCheck };
    }
}
