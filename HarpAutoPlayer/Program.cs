using Avalonia;
using HarpAutoPlayer;

internal static class Program
{
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia 配置，不要移除；也用于可视化设计器。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
