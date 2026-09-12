using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace HarpAutoPlayer.Engine;

/// <summary>
/// 检查 GitHub 上有没有新版本（后台静默进行，不影响使用）。
/// 只在有新版时由界面提示一句，点击可跳转到 Release 页面。
/// </summary>
public static class AutoUpdate
{
    private const string Owner = "ChickenD233";
    private const string Repo = "harmonica-auto-player";

    /// <summary>最新 Release 页面（用于跳转下载）。</summary>
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    /// 当前程序版本（如 1.0.7）。
    /// 注意：csproj 的 &lt;Version&gt;1.0.7&lt;/Version&gt; 映射到 AssemblyVersion 时是
    /// 1.0.7.0，即 Major=1 / Minor=0 / **Build=7**（Build 就是第三段，不是"内部版本号"），
    /// 所以第三段必须取 Build —— 取成 Minor 会得到 1.0.0，导致每次启动都误报有新版。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0.0";
            // 用 Math.Max 兜底：未显式指定版本时 Build/Revision 可能是 -1
            return $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
        }
    }

    public sealed class Result
    {
        public bool HasUpdate { get; init; }
        public string LatestTag { get; init; } = "";
        public string CurrentTag { get; init; } = "";
        public string ReleaseName { get; init; } = "";
        public string ReleaseUrl { get; init; } = "";
        public bool Skipped { get; init; }       // 用户选了"跳过这个版本"
        public string? Error { get; init; }      // 网络失败等（静默处理，不打扰用户）
    }

    /// <summary>
    /// 查询最新 Release 并与当前版本比较。
    /// <paramref name="skippedTag"/> 为用户此前选择"跳过"的版本号；
    /// 若最新版正好是被跳过的那个，则只返回 HasUpdate=false（可再次询问的机会由界面控制）。
    /// </summary>
    public static async Task<Result> CheckAsync(string? skippedTag = null,
                                                CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HarpAutoPlayer-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result { Error = $"HTTP {(int)resp.StatusCode}", CurrentTag = CurrentVersion };

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                                             .ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string name = root.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
            string html = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";

            bool newer = IsNewer(tag, CurrentVersion);
            return new Result
            {
                HasUpdate = newer,
                LatestTag = tag.TrimStart('v', 'V'),
                CurrentTag = CurrentVersion,
                ReleaseName = name,
                ReleaseUrl = string.IsNullOrEmpty(html) ? ReleasesUrl : html,
                Skipped = newer && !string.IsNullOrEmpty(skippedTag) &&
                          string.Equals(tag.TrimStart('v', 'V'), skippedTag.TrimStart('v', 'V'),
                                        StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            // 没网 / 被墙 / 超时都属正常，静默忽略
            return new Result { Error = ex.GetType().Name, CurrentTag = CurrentVersion };
        }
    }

    /// <summary>版本号比较：latest 是否比 current 新（按 x.y.z 逐段数值比较）。</summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = Parse(latest);
        var b = Parse(current);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    private static int[] Parse(string v)
    {
        var parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
        var outv = new int[3];
        for (int i = 0; i < 3 && i < parts.Length; i++)
            int.TryParse(parts[i], out outv[i]);
        return outv;
    }

    /// <summary>用系统默认浏览器打开链接（跳转到 Release 页面下载）。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，界面上也会把链接文字显示出来供手动复制
        }
    }
}
