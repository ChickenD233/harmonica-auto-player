using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameInstrumentPlayer.Profiles;

/// <summary>
/// 乐器方案的读写。内置方案写在代码里，用户方案放在
/// %LOCALAPPDATA%\GameInstrumentPlayer\profiles\*.json（macOS/Linux 开发时放在 ~/.config 下）。
/// 每个方案一个文件，方便用户直接分享与备份。
/// </summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameInstrumentPlayer", "profiles");

    /// <summary>用户方案的保存路径。</summary>
    public static string PathOf(string id) => Path.Combine(DirPath, SafeName(id) + ".json");

    private static string SafeName(string id)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (char c in id) sb.Append(bad.Contains(c) ? '_' : c);
        string s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "profile" : s;
    }

    /// <summary>读入全部用户方案。损坏的文件跳过，不打断启动。</summary>
    public static List<InstrumentProfile> LoadUserProfiles()
    {
        var list = new List<InstrumentProfile>();
        try
        {
            if (!Directory.Exists(DirPath)) return list;
            foreach (string file in Directory.EnumerateFiles(DirPath, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<InstrumentProfile>(File.ReadAllText(file), Json);
                    if (p == null) continue;
                    if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Path.GetFileNameWithoutExtension(file);
                    p.UserDefined = true;
                    if (p.Validate().Length != 0) continue;   // 不合法的方案不进列表
                    list.Add(p);
                }
                catch
                {
                    // 单个损坏文件不影响其它方案
                }
            }
        }
        catch
        {
            // 目录不可读时只用内置方案
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>保存一个方案。返回是否成功。</summary>
    public static bool Save(InstrumentProfile profile, out string error)
    {
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = profile.LayoutFingerprint();
            profile.UserDefined = true;
            string check = profile.Validate();
            if (check.Length != 0) { error = check; return false; }

            Directory.CreateDirectory(DirPath);
            File.WriteAllText(PathOf(profile.Id), JsonSerializer.Serialize(profile, Json));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>删除一个用户方案。</summary>
    public static bool Delete(InstrumentProfile profile, out string error)
    {
        error = "";
        try
        {
            string path = PathOf(profile.Id);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把一个方案写成 JSON 文本（分享给别人）。</summary>
    public static string ToJson(InstrumentProfile profile) => JsonSerializer.Serialize(profile, Json);

    /// <summary>从 JSON 文本读回一个方案。</summary>
    public static bool TryFromJson(string text, out InstrumentProfile? profile, out string error)
    {
        profile = null;
        error = "";
        try
        {
            var p = JsonSerializer.Deserialize<InstrumentProfile>(text, Json);
            if (p == null) { error = "文件里没有方案内容。"; return false; }
            if (p.Rows.Count == 0) { error = "文件里没有琴键布局。"; return false; }
            if (p.NoteOffsets.Count == 0) p.NoteOffsets = BuiltInProfiles.Major.ToList();
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = p.LayoutFingerprint();
            profile = p;
            return true;
        }
        catch (Exception ex)
        {
            error = "不是有效的方案文件：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 载入界面要用的完整清单：内置方案在前，用户方案在后。
    /// 如果用户的方案和某个内置方案布局完全一样，就不再重复显示该内置方案。
    /// </summary>
    public static (List<InstrumentProfile> All, List<InstrumentProfile> BuiltIn, List<InstrumentProfile> User)
        LoadAll()
    {
        var builtIn = BuiltInProfiles.All();
        var user = LoadUserProfiles();

        var userIds = new HashSet<string>(user.Select(u => u.Id), StringComparer.OrdinalIgnoreCase);
        var userLayouts = new HashSet<string>(user.Select(u => u.LayoutFingerprint()), StringComparer.OrdinalIgnoreCase);
        var showBuiltIn = builtIn.Where(b => !userLayouts.Contains(b.LayoutFingerprint())).ToList();

        var all = new List<InstrumentProfile>();
        all.AddRange(showBuiltIn);
        all.AddRange(user);
        return (all, showBuiltIn, user);
    }
}
