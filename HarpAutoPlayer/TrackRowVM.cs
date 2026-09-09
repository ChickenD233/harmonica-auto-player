using System.ComponentModel;
using System.Runtime.CompilerServices;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer;

/// <summary>轨道候选在表格里的一行。</summary>
public sealed class TrackRowVM : INotifyPropertyChanged
{
    private bool _isMain;
    private bool _isMix;
    private int _mixRank;

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
    public string DurationText => $"{Candidate.DurationSec:F1}s";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
