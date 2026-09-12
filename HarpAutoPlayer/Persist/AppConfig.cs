using System.Text.Json;

namespace HarpAutoPlayer.Persist;

/// <summary>用户设置：退出后记住，下次启动自动恢复。</summary>
public sealed class AppConfig
{
    public int Speed { get; set; } = 100;          // %
    public int Transpose { get; set; } = 0;        // 半音
    public int CountdownIndex { get; set; } = 1;   // 0秒/3/5/10
    public int ControlHotkeyIndex { get; set; } = 6; // 统一控制键（默认 F6：开始/暂停/继续）
    public int RewindHotkeyIndex { get; set; } = 5;  // 后退热键（默认 F5）
    public int ForwardHotkeyIndex { get; set; } = 7; // 前进热键（默认 F7）
    public bool ChordRoot { get; set; } = true;
    public bool Breath { get; set; } = false;
    public bool VocalExtract { get; set; } = false;   // 人声旋律提取（伴奏混同轨时）
    public bool TrimLead { get; set; } = true;        // 去除开头空拍（首音平移到 0 秒）
    public bool FirstRunDone { get; set; } = false;   // 首次“快速上手”是否已看过
    public bool AutoMinimizeOnPlay { get; set; } = true;  // 播放开始后自动最小化窗口
    public int TimingIndex { get; set; } = 1;         // 输入兼容档位：0稳健/1标准/2极限
    public string SkippedUpdateTag { get; set; } = "";   // 用户选择“跳过”的版本号（空=不跳过）

    private static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarpAutoPlayer");

    private static string FilePath => Path.Combine(DirPath, "settings.json");

    public static AppConfig Load()
    {
        bool existed = File.Exists(FilePath);
        try
        {
            if (existed)
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg != null)
                {
                    // 老用户升级：设置文件已存在就不算“首次”，不弹快速上手
                    cfg.FirstRunDone = true;
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏则用默认 */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写失败忽略 */ }
    }
}

/// <summary>本地运行日志（便于回传排查）。</summary>
public static class LogFile
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    private static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarpAutoPlayer");

    private static string FilePath => Path.Combine(DirPath, "play.log");

    public static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirPath);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    try { File.Copy(FilePath, FilePath + ".old", true); } catch { }
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { /* 日志失败不影响使用 */ }
    }
}
