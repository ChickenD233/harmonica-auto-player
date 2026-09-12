using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace HarpAutoPlayer;

/// <summary>
/// 【开发用，可删】UI 快照钩子。
///
/// 设了环境变量 <c>HARP_UI_SNAPSHOT=/path/out.png</c> 时：等首帧布局完成后，把窗口内容
/// 用 Skia 渲染成 PNG（2 倍缩放）再退出，用于在没有显示器/屏幕锁定的情况下检查界面样式。
/// 不设该变量时本文件不产生任何行为；不需要了就把本文件删掉，并删掉 MainWindow
/// 构造函数末尾的那一行 <c>InstallDevSnapshot(this);</c>。
/// </summary>
public partial class MainWindow
{
    internal static void InstallDevSnapshot(MainWindow window)
    {
        var path = Environment.GetEnvironmentVariable("HARP_UI_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(path)) return;

        window.Opened += (_, _) =>
        {
            // 等一帧多一点，确保 Bounds/布局都已就绪
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    // 首次启动的“快速上手”浮层会盖住主界面：先关掉拍主界面，再拍浮层
                    if (window.QuickStartOverlay != null)
                        window.QuickStartOverlay.IsVisible = false;
                    Capture(window, path);

                    if (window.QuickStartOverlay != null)
                    {
                        window.QuickStartOverlay.IsVisible = true;
                        Capture(window, Suffix(path, "-quickstart"));
                    }
                    Console.WriteLine($"UI snapshot saved: {path}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("UI snapshot failed: " + ex);
                }
                Environment.Exit(0);
            };
            timer.Start();
        };
    }

    /// <summary>
    /// 把窗口内容渲染成 PNG。用 96 DPI + 与布局等大的画布，几何与真实窗口 1:1 对应
    /// （试过 192 DPI + 双倍画布，会被窗口自身的 RenderScaling 再乘一次、只剩左上角，别用）。
    /// </summary>
    private static void Capture(MainWindow window, string path)
    {
        var root = (Visual?)window.Content ?? window;
        int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
        int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
        Console.WriteLine($"bounds={w}x{h} windowScaling={window.RenderScaling}");
        Save(root, path, new PixelSize(w, h), new Vector(96, 96));
    }

    private static void Save(Visual root, string path, PixelSize size, Vector dpi)
    {
        var rtb = new RenderTargetBitmap(size, dpi);
        rtb.Render(root);
        rtb.Save(path);
    }

    private static string Suffix(string path, string suffix)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path + suffix : path[..dot] + suffix + path[dot..];
    }
}
