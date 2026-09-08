using System.Text.Json;

namespace HarpAutoPlayer.Persist;

/// <summary>用户设置：退出后记住，下次启动自动恢复。</summary>
public sealed class AppConfig
{
    public int Speed { get; set; } = 100;          // %
    public int Transpose { get; set; } = 0;        // 半音
    public int CountdownIndex { get; set; } = 1;   // 0秒/3/5/10
    public int StartHotkeyIndex { get; set; } = 5; // F5
    public int PauseHotkeyIndex { get; set; } = 6; // F6
    public bool ChordRoot { get; set; } = true;
    public bool Breath { get; set; } = false;

    private static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarpAutoPlayer");

    private static string FilePath => Path.Combine(DirPath, "settings.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg != null) return cfg;
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
