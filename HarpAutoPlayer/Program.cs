using Avalonia;
using HarpAutoPlayer;

internal static class Program
{
    // 单实例锁：第二个实例启动时直接退出，避免两个程序同时按键
    private static System.IO.FileStream? _singleLock;

    // 初始化代码。不要使用任何可能依赖 Avalonia、第三方库或
    // 其它服务端代码的 API 来初始化应用。
    [STAThread]
    public static void Main(string[] args)
    {
        // 任何未捕获异常都写入本地日志，便于回传排查
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { HarpAutoPlayer.Persist.LogFile.Append("[FATAL] " + (e.ExceptionObject?.ToString() ?? "未知异常")); }
            catch { }
        };

        if (!TryAcquireSingleInstance())
        {
            try { HarpAutoPlayer.Persist.LogFile.Append("检测到已有一个实例在运行，本实例直接退出。"); }
            catch { }
            return;   // 已有一个在跑 → 退出，防止两个程序同时模拟按键
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>通过独占锁文件保证单实例（跨平台）。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarpAutoPlayer");
            System.IO.Directory.CreateDirectory(dir);
            string lockFile = System.IO.Path.Combine(dir, "instance.lock");
            _singleLock = new System.IO.FileStream(lockFile,
                System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite,
                System.IO.FileShare.None);   // 第二个进程打开会失败
            return true;
        }
        catch (System.IO.IOException)
        {
            return false;   // 已被占用
        }
        catch
        {
            return true;    // 其它异常不阻塞运行（尽量保证能启动）
        }
    }

    // Avalonia 配置，不要移除；也用于可视化设计器。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
