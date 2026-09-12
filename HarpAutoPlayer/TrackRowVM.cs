using System.ComponentModel;
using System.Runtime.CompilerServices;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer;

public sealed class TrackRowVM : INotifyPropertyChanged
{
    private bool _isMain;
    private bool _isMix;
    private int _mixRank;
    private bool _isRecommended;

    public TrackRowVM(MidiCandidate candidate)
    {
        Candidate = candidate;
    }

    public MidiCandidate Candidate { get; }

    public bool IsMain
    {
        get => _isMain;
        set
        {
            if (_isMain == value) return;
            _isMain = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStar));
        }
    }

    /// <summary>勾选进“多声部合奏”。</summary>
    public bool IsMix
    {
        get => _isMix;
        set
        {
            if (_isMix == value) return;
            _isMix = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStar));
        }
    }

    /// <summary>参与合奏时显示优先级编号，不再显示主旋律圆点。</summary>
    public bool ShowStar => IsMain && !IsMix;

    /// <summary>合奏优先级：1 最优先，0=未参与合奏。</summary>
    public int MixRank
    {
        get => _mixRank;
        set
        {
            if (_mixRank == value) return;
            _mixRank = value;
            OnPropertyChanged();
        }
    }

    public string TrackLabel => Candidate.TrackLabel;
    public string ChannelLabel => Candidate.ChannelLabel;
    public string Name => Candidate.Name;
    public int NoteCount => Candidate.NoteCount;
    public string RangeLabel => Candidate.RangeLabel;

    /// <summary>时长显示：不足 1 分钟显示秒，否则 分:秒。</summary>
    public string DurationText
    {
        get
        {
            double d = Candidate.DurationSec;
            if (d < 1) return "—";
            if (d < 60) return $"{d:F0}秒";
            int m = (int)(d / 60);
            int s = (int)d % 60;
            return $"{m}分{s:00}秒";
        }
    }

    /// <summary>打击乐等不适合作为口琴主旋律的轨道。</summary>
    public bool IsPercussion =>
        Candidate.Channel == 9 ||
        Candidate.Name.Contains("打击", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("鼓", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("drum", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("percussion", StringComparison.OrdinalIgnoreCase);

    /// <summary>能否选作主旋律（打击乐整行禁用）。</summary>
    public bool IsPlayable => !IsPercussion;

    /// <summary>载入时被自动推荐为主旋律轨。</summary>
    public bool IsRecommended
    {
        get => _isRecommended;
        set
        {
            if (_isRecommended == value) return;
            _isRecommended = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(NameWeight));
        }
    }

    /// <summary>推荐轨名称带 ★ 标记。</summary>
    public string DisplayName => IsRecommended ? Name + " ★推荐" : Name;

    public Avalonia.Media.FontWeight NameWeight =>
        IsRecommended ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
