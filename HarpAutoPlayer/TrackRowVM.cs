using System.ComponentModel;
using System.Runtime.CompilerServices;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer;

/// <summary>轨道候选在表格里的一行。</summary>
public sealed class TrackRowVM : INotifyPropertyChanged
{
    private bool _isMain;

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
