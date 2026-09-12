using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HarpAutoPlayer.Input;

/// <summary>
/// 播放前自检：判断"模拟按键能不能真的送进游戏"。
/// 只做检测与提示，不改动任何系统设置。
/// </summary>
public static class PreflightCheck
{
    // ---------------- 严重级别 ----------------
    public enum Level { Ok, Warn, Fail }

    public sealed record Item(string Title, Level Level, string Detail, string Advice);

    public sealed class Report
    {
        public List<Item> Items { get; } = new();
        public bool HasProblem => Items.Any(i => i.Level != Level.Ok);

        public void Add(string title, Level level, string detail, string advice = "")
            => Items.Add(new Item(title, level, detail, advice));
    }

    // ---------------- P/Invoke ----------------
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>当前进程是否以管理员身份运行。</summary>
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

    /// <summary>指定进程是否以管理员身份运行；取不到信息时返回 null。</summary>
    public static bool? IsProcessElevated(uint pid)
    {
        if (!OperatingSystem.IsWindows() || pid == 0) return null;
        IntPtr proc = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return null;
            if (!OpenProcessToken(proc, TOKEN_QUERY, out token)) return null;
            if (!GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _))
                return null;
            return elevated != 0;
        }
        catch { return null; }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (proc != IntPtr.Zero) CloseHandle(proc);
        }
    }

    /// <summary>当前前台窗口的标题、进程名、进程号。</summary>
    public static (IntPtr Hwnd, string Title, string ProcessName, uint Pid) ForegroundInfo()
    {
        if (!OperatingSystem.IsWindows()) return (IntPtr.Zero, "", "", 0);
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return (IntPtr.Zero, "", "", 0);

        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);

        GetWindowThreadProcessId(h, out uint pid);
        string procName = "";
        try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

        return (h, sb.ToString(), procName, pid);
    }

    /// <summary>
    /// 前台窗口所属线程的键盘布局。低 16 位是 LANGID：
    /// 0x0804 = 简体中文，0x0404 = 繁体中文，0x0411 = 日文，0x0409 = 美式英文…
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

    /// <summary>
    /// 运行自检。<paramref name="selfHwnd"/> 为本程序自己的窗口句柄，
    /// 用于识别"按键会被发到本程序而不是游戏"的情况。
    /// </summary>
    public static Report Run(IntPtr selfHwnd)
    {
        var r = new Report();

        if (!OperatingSystem.IsWindows())
        {
            r.Add("平台", Level.Warn, "当前不是 Windows，无法模拟输入", "仅用于流程演示");
            return r;
        }

        // ① 管理员权限
        bool selfAdmin = IsSelfElevated();
        if (selfAdmin)
            r.Add("管理员权限", Level.Ok, "本程序已以管理员身份运行");
        else
            r.Add("管理员权限", Level.Warn,
                  "本程序未以管理员身份运行",
                  "若游戏以管理员运行，Windows 会拦截模拟按键（UIPI）。建议以管理员身份运行本程序");

        // ② 前台窗口 + 目标进程权限
        var (hwnd, title, procName, pid) = ForegroundInfo();
        if (hwnd == IntPtr.Zero)
        {
            r.Add("前台窗口", Level.Warn, "读不到前台窗口", "播放前请点一下游戏画面");
        }
        else if (hwnd == selfHwnd)
        {
            r.Add("前台窗口", Level.Fail,
                  $"当前前台是本程序自己（{procName}）",
                  "按键会发到本程序窗口而不是游戏。请点一下游戏画面，或用倒计时切过去");
        }
        else
        {
            var targetAdmin = IsProcessElevated(pid);
            string adminNote = targetAdmin switch
            {
                true => "（该进程以管理员运行）",
                false => "",
                null => "（权限未知）"
            };
            r.Add("前台窗口", Level.Ok,
                  $"{procName} —— {Truncate(title, 46)}{adminNote}");

            if (targetAdmin == true && !selfAdmin)
                r.Add("权限匹配", Level.Fail,
                      "游戏以管理员运行，而本程序不是",
                      "这种组合下模拟按键会被系统拦截，几乎必然\"发不进游戏\"。请以管理员身份重启本程序");
            else if (targetAdmin == false && selfAdmin)
                r.Add("权限匹配", Level.Ok, "本程序权限不低于游戏，可以正常投递");
        }

        // ③ 输入法（中文/日文等 IME 会截走按键）
        ushort lang = ForegroundKeyboardLayoutId();
        if (lang == 0)
        {
            r.Add("输入法", Level.Warn, "读不到当前键盘布局", "请手动确认已切到英文输入法");
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
            r.Add("输入法", Level.Fail,
                  $"前台窗口当前是{name}输入法",
                  "中文/日文输入法会截走按键（尤其拼音未上屏时）。请按 Win+空格 或 Shift 切到英文");
        }
        else
        {
            r.Add("输入法", Level.Ok, $"前台窗口是英文/其它非 IME 布局（0x{lang:X4}）");
        }

        return r;
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "（无标题）" : (s.Length <= n ? s : s[..n] + "…");

    /// <summary>把自检结果拼成可直接写进日志的多行文本。</summary>
    public static IEnumerable<string> ToLogLines(Report r)
    {
        foreach (var i in r.Items)
        {
            string mark = i.Level switch
            {
                Level.Ok => "✔",
                Level.Warn => "!",
                _ => "✘"
            };
            yield return $"  [{mark}] {i.Title}：{i.Detail}" +
                         (i.Level != Level.Ok && i.Advice.Length > 0 ? $"　→ {i.Advice}" : "");
        }
    }
}
