using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using HarpAutoPlayer.Engine;
using HarpAutoPlayer.Input;
using HarpAutoPlayer.Midi;
using HarpAutoPlayer.Persist;

namespace HarpAutoPlayer;

/// <summary>
/// 主窗口：选择主旋律、播放/暂停/停止、热键、进度跳转、实时变速/移调，
/// 支持托盘后台、记住设置、本地日志与呼吸休止选项。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackRowVM> _tracks = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly List<TrackRowVM> _mixOrder = new();   // 勾选合奏的顺序 = 主次（先勾=主）

    private ParsedMidi? _parsed;
    private TrackRowVM? _selected;
    private List<MappedNote> _playNotes = new();
    private PlaybackEngine? _engine;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _saveDeb;
    private readonly DispatcherTimer _previewDeb;
    private int _countdownLeft;
    private bool _busy;
    private bool _seeking;          // 用户正在拖进度条
    private bool _liveQueued;       // 已排队待应用的实时移调
    private IntPtr _gameHwnd;       // 播放期间记住的游戏窗口（用于停止时把焦点还给它）
    private double _removedLeadSec; // “去除开头空拍”实际剪掉的秒数（本轮）
    private readonly AppConfig _cfg;
    private TrayIcon? _tray;
    private bool _quitNow;

    private static readonly int[] CountdownOptions = { 0, 3, 5, 10 };
    private const int FixedLeadMs = 25;

    private bool _uiReady;   // 构造期各下拉框初始化会触发 *Changed，此时不应写日志
    private string _updateUrl = "";     // 有新版本时的下载页
    private string _updateTag = "";     // 有新版本时的版本号

    public MainWindow()
    {
        InitializeComponent();

        TrackList.ItemsSource = _tracks;
        LogList.ItemsSource = _log;

        // 倒计时下拉：0/3/5/10 秒，默认 3 秒
        CountdownCombo.ItemsSource = new List<string> { "0秒(立即)", "3秒", "5秒", "10秒" };
        CountdownCombo.SelectedIndex = 1;

        // 统一控制热键下拉：F1..F12（默认 F6 = 开始/暂停/继续）
        var fkeys = new List<string> { "无" };
        for (int i = 1; i <= 12; i++) fkeys.Add("F" + i);
        HotkeyControlCombo.ItemsSource = fkeys;
        HotkeyControlCombo.SelectedIndex = 6;   // F6

        // 输入兼容档位：稳健 / 标准 / 极限（决定修饰键与音键之间的物理时间余量）
        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;          // 标准

        // 导出格式：罗技 G HUB 脚本 / 通用 CSV / 纯文本按键表
        ExportFormatCombo.ItemsSource = new List<string>
        {
            "罗技 G HUB 脚本 (.lua)", "通用 CSV (.csv)", "纯文本按键表 (.txt)"
        };
        ExportFormatCombo.SelectedIndex = 0;

        // —— 记住上次设置 ——
        _cfg = AppConfig.Load();
        CountdownCombo.SelectedIndex = Math.Clamp(_cfg.CountdownIndex, 0, 3);
        HotkeyControlCombo.SelectedIndex = Math.Clamp(_cfg.ControlHotkeyIndex, 0, 12);
        SliderSpeed.Value = Math.Clamp(_cfg.Speed, 50, 200);
        SliderTranspose.Value = Math.Clamp(_cfg.Transpose, -10, 10);
        ChkChordRoot.IsChecked = _cfg.ChordRoot;
        ChkBreath.IsChecked = _cfg.Breath;
        ChkVocalExtract.IsChecked = _cfg.VocalExtract;
        ChkTrimLead.IsChecked = _cfg.TrimLead;
        ChkAutoMinimize.IsChecked = _cfg.AutoMinimizeOnPlay;
        TimingCombo.SelectedIndex = Math.Clamp(_cfg.TimingIndex, 0, 2);

        _saveDeb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDeb.Tick += (_, _) =>
        {
            _saveDeb!.Stop();
            SaveSettings();
        };

        if (Input.GlobalHotkeys.IsAvailable)
        {
            Input.GlobalHotkeys.Status += s => UiPost(() => InsertLog(s));
            Input.GlobalHotkeys.KeyState += OnGlobalKeyWorker;
            ReconfigureHotkeys();
            Input.GlobalHotkeys.Start();
        }

        // 进度条：捕获“已被滑块内部处理”的指针事件，实现任意位置点击/拖动跳转
        SliderProgress.AddHandler(InputElement.PointerPressedEvent, Progress_PointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerMovedEvent, Progress_PointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerReleasedEvent, Progress_PointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        _previewDeb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDeb.Tick += (_, _) =>
        {
            _previewDeb.Stop();
            RefreshPreview();
        };

        UpdateSettingLabels();
        RefreshPreview();
        SetIdleHint();
        UpdateTransportUi();
        _uiReady = true;

        // 后台静默检查新版本（不阻塞界面；没网就悄悄跳过）
        _ = CheckUpdateAsync();

        InsertLog("欢迎使用 口琴自动演奏器（三角洲行动）");
        InsertLog("用法：打开 MIDI 文件 → 在左侧单击一行作为主旋律 → 按 F6（或点「▶ 播放」），倒计时内切到游戏并装备口琴即可。");
        InsertLog("控制热键：F6 = 空闲开始 / 播放暂停 / 暂停继续（游戏中直接生效，可在“控制热键”里改）。");
        if (OperatingSystem.IsWindows())
        {
            SetupTray();
            Opened += (_, _) => EnsureTray();
        }
        Opened += (_, _) => ShowQuickStartOnce();
    }

    // ================= 首次启动“快速上手” =================

    private void ShowQuickStartOnce()
    {
        if (_cfg == null || _cfg.FirstRunDone || QuickStartOverlay == null) return;
        QuickStartOverlay.IsVisible = true;
    }

    private void QuickStartOk_Click(object? sender, RoutedEventArgs e)
    {
        if (QuickStartOverlay != null) QuickStartOverlay.IsVisible = false;
        if (_cfg != null)
        {
            _cfg.FirstRunDone = true;
            _cfg.Save();
        }
    }

    // ================= 全局热键 =================

    private void Hotkey_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (!Input.GlobalHotkeys.IsAvailable) return;
        ReconfigureHotkeys();
    }

    private void CountdownCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
    }

    private void ReconfigureHotkeys()
    {
        int idx = Math.Max(0, HotkeyControlCombo.SelectedIndex);
        int code = idx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(idx) : 0;
        Input.GlobalHotkeys.SetActive(code == 0 ? Array.Empty<int>() : new[] { code });
    }

    private void OnGlobalKeyWorker(int code, bool down)
    {
        UiPost(() => HandleGlobalKey(code));
    }

    private void HandleGlobalKey(int code)
    {
        int idx = Math.Max(0, HotkeyControlCombo.SelectedIndex);
        int hot = idx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(idx) : 0;
        if (hot != 0 && code == hot) ToggleControl();
    }

    /// <summary>
    /// 统一控制键（默认 F6）：空闲按 = 开始；倒计时中再按 = 取消开始；
    /// 播放中按 = 暂停；暂停中按 = 继续。界面 ▶ 按钮与托盘项都走这里，行为一致。
    /// </summary>
    private void ToggleControl()
    {
        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            TogglePause();
            return;
        }
        if (_busy)   // 正在倒计时：再按一次 = 取消本次开始
        {
            InsertLog("已取消本次开始（控制键），可换好歌后再按一次重新开始。");
            _countdownTimer?.Stop();
            _countdownTimer = null;
            ResetUi();
            return;
        }
        RequestPlay();
    }

    private void TogglePause()
    {
        var eng = _engine;
        if (eng is not { IsRunning: true })
        {
            InsertLog("当前没有在播放，无法暂停。");
            return;
        }
        if (eng.IsPaused)
        {
            eng.Resume();
            LblStatus.Foreground = Avalonia.Media.Brushes.SeaGreen;
            LblStatus.FontSize = 21;
            LblStatus.Text = "演奏中…";
        }
        else
        {
            eng.Pause();
            LblStatus.Foreground = Avalonia.Media.Brushes.Crimson;
            LblStatus.Text = "已暂停 —— 按 F6 或点「▶ 继续」重新开始";
        }
        UpdateTransportUi();
    }

    // ================= 状态区 / 按钮提示 =================

    /// <summary>空闲时的状态区提示（人话，不是干巴巴的空白）。</summary>
    private void SetIdleHint()
    {
        LblStatus.Foreground = Avalonia.Media.Brushes.Gray;
        LblStatus.FontSize = 15;
        LblStatus.Text = "打开 MIDI 并点选主旋律 → 按 F6 或点 ▶ 播放（同一个键：再按暂停 / 再按继续）";
    }

    /// <summary>
    /// 按当前状态统一播放按钮与热键提示：
    /// 空闲=▶播放(F6)；播放中=⏸暂停；暂停中=▶继续。停止按钮倒计时里可用。
    /// </summary>
    private void UpdateTransportUi()
    {
        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            BtnPlay.IsEnabled = true;
            BtnStop.IsEnabled = true;
            BtnPlay.Content = eng.IsPaused ? "▶ 继续 (F6)" : "⏸ 暂停";
            TxtHotHint.Text = eng.IsPaused ? "F6 继续" : "F6 暂停";
            return;
        }
        BtnPlay.Content = "▶ 播放 (F6)";
        BtnStop.IsEnabled = _busy;   // 倒计时中允许点停止取消
        BtnPlay.IsEnabled = !_busy && ActiveRows().Count > 0 && BuildMapping().InRangeCount > 0;
        TxtHotHint.Text = "F6：开始 / 暂停 / 继续";
    }

    // ================= 日志 / 设置持久化 =================

    private void UiPost(Action a) => Dispatcher.UIThread.Post(a);

    private void InsertLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _log.Insert(0, line);
        while (_log.Count > 400) _log.RemoveAt(_log.Count - 1);
        Persist.LogFile.Append(line);   // 落盘
    }

    private void ScheduleSave()
    {
        _saveDeb?.Stop();
        _saveDeb?.Start();
    }

    private void SaveSettings()
    {
        if (_cfg == null) return;
        _cfg.Speed = (int)SliderSpeed.Value;
        _cfg.Transpose = (int)SliderTranspose.Value;
        _cfg.CountdownIndex = Math.Clamp(CountdownCombo.SelectedIndex, 0, 3);
        _cfg.ControlHotkeyIndex = Math.Clamp(HotkeyControlCombo.SelectedIndex, 0, 12);
        _cfg.ChordRoot = ChkChordRoot.IsChecked == true;
        _cfg.Breath = ChkBreath.IsChecked == true;
        _cfg.VocalExtract = ChkVocalExtract.IsChecked == true;
        _cfg.TrimLead = ChkTrimLead.IsChecked == true;
        _cfg.AutoMinimizeOnPlay = ChkAutoMinimize.IsChecked == true;
        _cfg.TimingIndex = Math.Clamp(TimingCombo.SelectedIndex, 0, 2);
        _cfg.Save();
    }

    private void UpdateSettingLabels()
    {
        if (TxtSpeed is null || TxtTranspose is null) return;
        TxtSpeed.Text = $"{SliderSpeed.Value:0}%";
        double tr = SliderTranspose.Value;
        TxtTranspose.Text = tr > 0 ? $"+{tr:0}" : $"{tr:0}";
    }

    private void SpeedChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSettingLabels();

        if (_engine is { IsRunning: true } eng)
        {
            if (ReferenceEquals(sender, SliderSpeed))
            {
                // 播放中实时变速：立即生效
                eng.Speed = SliderSpeed.Value / 100.0;
            }
            else if (ReferenceEquals(sender, SliderTranspose))
            {
                // 播放中实时移调：等拖动停顿 150ms 再换谱，避免逐刻度反复重建
                QueueLiveTranspose();
            }
            return;
        }

        if (!_busy && _previewDeb is not null)
        {
            _previewDeb.Stop();
            _previewDeb.Start();
        }
        ScheduleSave();
    }

    /// <summary>播放中移调：停顿后重建剩余音符并应用到引擎。</summary>
    private void QueueLiveTranspose()
    {
        if (_liveQueued) return;
        _liveQueued = true;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _liveTimer.Tick += (_, _) =>
        {
            _liveTimer!.Stop();
            _liveTimer = null;
            _liveQueued = false;
            ApplyLiveTranspose();
        };
        _liveTimer.Start();
    }

    private void ApplyLiveTranspose()
    {
        var eng = _engine;
        if (eng == null || _selected == null) return;

        var map = BuildMapping();
        var notes = map.Notes.Where(n => n.InRange).ToList();
        eng.UpdateNotes(notes);
        InsertLog($"移调 {CurrentTranspose:+#;-#;0}：可演奏 {notes.Count} 音" +
                  (map.SkipCount > 0 ? $" / 空拍 {map.SkipCount}" : ""));
        RefreshPreview();
    }

    // ================= 进度条拖拽（播放中跳转） =================

    private void Progress_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_engine == null || !_engine.IsRunning) return;
        _seeking = true;
        SeekThumbTo(e);            // 点哪跳到哪（不用先抓滑块）
    }

    private void Progress_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seeking || _engine == null) return;
        SeekThumbTo(e);            // 按住拖动 = 预览位置（不打断播放）
    }

    private void Progress_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        var eng = _engine;
        if (eng == null) return;
        double max = SliderProgress.Maximum;
        double frac = max > 0 ? SliderProgress.Value / max : 0;
        eng.SeekFraction(frac);    // 松手才真正跳转
        TxtTime.Text = $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s";
    }

    private void SeekThumbTo(PointerEventArgs e)
    {
        double w = SliderProgress.Bounds.Width;
        if (w <= 0) return;
        double x = e.GetPosition(SliderProgress).X;
        double frac = Math.Clamp(x / w, 0.0, 1.0);
        SliderProgress.Value = frac * SliderProgress.Maximum;
    }

    // ================= 文件载入 =================

    private async void BtnOpen_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 MIDI 文件",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("MIDI 文件")
                    {
                        Patterns = new List<string> { "*.mid", "*.midi", "*.kar", "*.rmi" }
                    },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            // 暂停/播放中也能换歌：载入新文件前先停掉当前播放，避免新旧串曲
            StopPlaybackForNewFile();

            try
            {
                var parsed = MidiLoader.Parse(path);
                _parsed = parsed;
                _tracks.Clear();
                _mixOrder.Clear();
                foreach (var c in parsed.Candidates) _tracks.Add(new TrackRowVM(c));

                LblFile.Text = System.IO.Path.GetFileName(path);
                InsertLog($"已载入 {System.IO.Path.GetFileName(path)}：{parsed.Candidates.Count} 个候选，时长 ≈ {parsed.DurationSec:F1}s");

                _selected = null;
                ChooseRecommendedTrack();
                RefreshPreview();
            }
            catch (Exception ex)
            {
                var parts = new List<string>();
                Exception? inner = ex;
                while (inner != null)
                {
                    parts.Add($"{inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                }
                InsertLog($"载入失败：{path}");
                InsertLog($"  原因：{string.Join("  <-  ", parts)}（错误码 0x{ex.HResult:X8}）");
            }
        }
        catch (Exception ex)
        {
            InsertLog($"打开文件对话框失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>载入新 MIDI 前，把还在播放/暂停的旧曲子停掉（松开按键、释放引擎）。</summary>
    private void StopPlaybackForNewFile()
    {
        if (_engine is not { IsRunning: true }) return;
        StopPlaybackNow();
        InsertLog("已停止当前播放（换歌）。");
    }

    // ================= 主旋律选择 =================

    /// <summary>
    /// 载入后自动挑一条最像主旋律的轨并选中（人不满意可再点其它行）。
    /// 依据：非打击乐、轨道名像旋律（旋律/人声/主唱/Lead…）、音域贴合口琴。
    /// </summary>
    private void ChooseRecommendedTrack()
    {
        TrackRowVM? best = null;
        double bestScore = double.MinValue;
        foreach (var r in _tracks)
        {
            if (!r.IsPlayable) continue;
            double s = ScoreCandidate(r);
            if (s > bestScore)
            {
                bestScore = s;
                best = r;
            }
        }
        if (best == null)
        {
            InsertLog("没有找到适合口琴的旋律轨（全是打击乐？），请手动点选一行试试。");
            return;
        }

        best.IsRecommended = true;
        TrackList.SelectedItem = best;   // 触发 SelectionChanged → SetMain → 高亮
        if (bestScore >= 20)
            InsertLog($"已自动选中推荐轨：{best.DisplayName}（非打击乐、最像旋律、音域贴合；想换就点其它行）。");
        else
            InsertLog($"已自动选中较合适的轨：{best.DisplayName}（音域贴合的不多，可再用「一键移调」调整）。");
    }

    private double ScoreCandidate(TrackRowVM r)
    {
        string name = r.Candidate.Name;
        double s = 0;

        // 轨道名像“旋律”的加分
        string[] melodyHints =
            { "旋律", "主旋律", "主唱", "人声", "女声", "男声", "独奏", "主音",
              "lead", "melod", "vocal", "vox", "solo", "sing" };
        foreach (var kw in melodyHints)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                s += 45;
                break;
            }
        }
        // 明显是伴奏/低音/吉他的减分
        string[] accompHints =
            { "伴奏", "和声", "和弦", "低音", "吉他", "钢琴伴", "节奏",
              "bass", "chord", "back", "guitar", "pad", "rhythm", "fx" };
        foreach (var kw in accompHints)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                s -= 35;
                break;
            }
        }

        var notes = r.Candidate.Notes;
        if (notes.Count > 0)
        {
            var map = NoteMapper.Map(notes, 0, null);
            s += 30.0 * map.InRangeCount / notes.Count;   // 音域贴合度（不抢先于名字线索）
            if (notes.Count < 8) s -= 20;                  // 太碎不像是能吹的歌
            s += Math.Min(notes.Count / 50.0, 8.0);        // 稍偏好完整曲目轨
        }
        return s;
    }

    private void TrackList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TrackList.SelectedItem is TrackRowVM row) SetMain(row);
    }

    private void SetMain(TrackRowVM? row)
    {
        if (row == null) return;
        foreach (var r in _tracks) r.IsMain = ReferenceEquals(r, row);
        _selected = row;
        RefreshPreview();
    }

    // ================= 映射与预览 =================

    private int CurrentTranspose => (int)SliderTranspose.Value;

    /// <summary>要演奏的行：勾了“合”就按勾选顺序合奏，否则用点选的那一行。</summary>
    private List<TrackRowVM> ActiveRows()
    {
        var mix = _mixOrder.Where(r => r.IsMix).ToList();
        if (mix.Count > 0) return mix;
        return _selected != null ? new List<TrackRowVM> { _selected } : new List<TrackRowVM>();
    }

    /// <summary>把当前要演奏的音符（含合奏/和弦取根）合并成一条线（未移调）。</summary>
    private List<RawNote> GetActiveRawNotes()
    {
        var rows = ActiveRows();
        if (rows.Count == 0)
        {
            _removedLeadSec = 0;
            return new List<RawNote>();
        }
        var voices = new List<(int Rank, RawNote Note)>();
        for (int k = 0; k < rows.Count; k++)
        {
            var rowNotes = rows[k].Candidate.Notes;
            if (ChkVocalExtract.IsChecked == true)
                rowNotes = NoteMapper.ExtractVocalMelody(rowNotes);   // 人声旋律提取（伴奏混同轨）
            else if (ChkChordRoot.IsChecked == true)
                rowNotes = MelodyExtractor.Extract(rowNotes);          // 自动提取主旋律（分声部 + 自动择一）
            foreach (var n in rowNotes) voices.Add((k + 1, n));  // 1 最优先
        }
        var merged = NoteMapper.MergeVoicesByPriority(voices);

        // “去除开头空拍”：把整条旋律平移到第一个音从 0 秒开始（MIDI 开头常有几小节休止）
        if (ChkTrimLead.IsChecked == true)
        {
            double before = merged.Count == 0 ? 0 : merged.Min(n => n.Start);
            merged = NoteMapper.TrimLeadingSilence(merged);
            double after = merged.Count == 0 ? 0 : merged.Min(n => n.Start);
            _removedLeadSec = Math.Max(0, before - after);
        }
        else
        {
            _removedLeadSec = 0;
        }
        return merged;
    }

    private MappingResult MapAt(int transpose) =>
        NoteMapper.Map(GetActiveRawNotes(), transpose, manualBaseOctave: null);

    private MappingResult BuildMapping() =>
        GetActiveRawNotes().Count == 0 ? new MappingResult() : MapAt(CurrentTranspose);

    /// <summary>一键移调：找让空拍（超音域）最少的移调量。</summary>
    // ================= 导出按键表 / 宏 =================

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var map = BuildMapping();
            var playable = map.Notes.Where(n => n.InRange).ToList();
            if (playable.Count == 0)
            {
                InsertLog("没有可演奏的音，无法导出。请先调整「移调」或换一行。");
                return;
            }

            var format = ExportFormatCombo.SelectedIndex switch
            {
                1 => MacroExporter.Format.KeystrokeCsv,
                2 => MacroExporter.Format.KeystrokeText,
                _ => MacroExporter.Format.LogitechGHub
            };
            double speed = SliderSpeed.Value / 100.0;
            var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);

            string songName = _parsed == null ? "" : Path.GetFileNameWithoutExtension(_parsed.FilePath);
            string content = MacroExporter.Build(playable, format, speed, timing, songName);

            var (nCount, nEvents, seconds) = MacroExporter.Summarize(playable, speed, timing);
            string suggested = (string.IsNullOrWhiteSpace(songName) ? "harp" : songName) +
                               MacroExporter.Extension(format);

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出按键表",
                SuggestedFileName = suggested,
                DefaultExtension = MacroExporter.Extension(format).TrimStart('.'),
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("导出文件") { Patterns = new List<string> { "*" + MacroExporter.Extension(format) } }
                }
            });
            if (file == null) return;

            string outPath = file.TryGetLocalPath() ?? "";
            if (string.IsNullOrEmpty(outPath))
            {
                InsertLog("导出失败：拿不到目标路径。");
                return;
            }
            await File.WriteAllTextAsync(outPath, content, new UTF8Encoding(true));

            InsertLog($"已导出按键表：{playable.Count} 个音 / {nEvents} 个事件 / 时长 {seconds:F1}s" +
                      $"（速度 {speed * 100:F0}%、时序档位 {timing.Name}）");
            InsertLog($"　文件：{outPath}");
            if (format == MacroExporter.Format.LogitechGHub)
                InsertLog("　罗技 G HUB 用法：G HUB → 设备 → 游戏与应用程序 → 添加游戏 → 编写脚本 → 编辑脚本 → 整段粘贴保存。");
            else
                InsertLog("　提示：雷蛇 Synapse 的宏文件是私有格式（无官方规范），请用本文件配合其宏录制，或使用支持导入按键时序的工具。");
        }
        catch (Exception ex)
        {
            InsertLog($"导出失败：{ex.Message}");
        }
    }

    private void BtnAutoTranspose_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || GetActiveRawNotes().Count == 0) return;

        int best = 0;
        int bestSkip = int.MaxValue;
        for (int t = -6; t <= 6; t++)
        {
            var m = MapAt(t);
            if (m.SkipCount < bestSkip ||
                (m.SkipCount == bestSkip && Math.Abs(t) < Math.Abs(best)))
            {
                bestSkip = m.SkipCount;
                best = t;
            }
        }

        SliderTranspose.Value = best;   // 触发滑块事件：保存设置并刷新预览
        string sign = best > 0 ? "+" : "";
        if (bestSkip == 0)
            InsertLog($"一键移调：整体 {sign}{best} 半音后全部音都在口琴音域内。");
        else
            InsertLog($"一键移调：整体 {sign}{best} 半音后仍剩 {bestSkip} 个音超音域（旋律本身跨度过大，超出部分仍会空拍）。");
        RefreshPreview();
    }

    /// <summary>
    /// 勾选“合”：勾选先后即主次（先勾=1 主，后勾=2、3…）。
    /// 勾完立即刷新，勾选本身就能点播放，无需再点行。
    /// </summary>
    private void Mix_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TrackRowVM row)
        {
            bool on = cb.IsChecked == true;
            row.IsMix = on;   // 保证模型状态一致
            if (on)
            {
                if (!_mixOrder.Contains(row)) _mixOrder.Add(row);
            }
            else
            {
                _mixOrder.Remove(row);
            }
        }
        // 兜底同步 IsMix 状态
        foreach (var r in _tracks)
            if (r.IsMix && !_mixOrder.Contains(r)) _mixOrder.Add(r);
        _mixOrder.RemoveAll(r => !r.IsMix);

        for (int i = 0; i < _mixOrder.Count; i++) _mixOrder[i].MixRank = i + 1; // 1=主，2=次，3=更次
        foreach (var r in _tracks)
            if (!_mixOrder.Contains(r)) r.MixRank = 0;

        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        RefreshPreview();   // 立即刷新，勾完即可播放
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        _previewDeb.Start();
    }

    private void Breath_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
    }

    // ================= 播放前自检 =================

    /// <summary>
    /// 检查"模拟按键能不能真的送进游戏"，并把结果写进日志。
    /// 检查项：管理员权限、前台窗口是不是本程序、游戏与本程序的权限是否匹配、前台输入法。
    /// </summary>
    private void RunPreflight()
    {
        try
        {
            var report = PreflightCheck.Run(SelfHwnd);
            bool hasProblem = report.HasProblem;

            InsertLog(hasProblem ? "播放前自检 —— 发现需要注意的地方：" : "播放前自检：全部正常。");
            foreach (var line in PreflightCheck.ToLogLines(report)) InsertLog(line);

            if (hasProblem)
                LblWarn.Text = "播放前自检发现问题：请看右下日志（多半是没用管理员运行、或输入法还是中文）。";
            else
                LblWarn.Text = "";
        }
        catch (Exception ex)
        {
            InsertLog($"播放前自检未能完成（不影响播放）：{ex.Message}");
        }
    }

    // ================= 自动检查更新 =================

    /// <summary>
    /// 后台查询 GitHub 最新 Release。有新版本 → 顶部显示提示条（点击跳转下载）；
    /// 没有 / 没网 / 被墙都静默忽略，绝不打扰使用。
    /// </summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            var r = await AutoUpdate.CheckAsync(_cfg?.SkippedUpdateTag);
            if (r.Error != null || !r.HasUpdate || r.Skipped) return;

            _updateUrl = r.ReleaseUrl;
            _updateTag = r.LatestTag;

            UiPost(() =>
            {
                TxtUpdate.Text = $"发现新版本 v{r.LatestTag}（当前 v{r.CurrentTag}）——" +
                                 "点此打开下载页，或到日志区复制链接。";
                UpdateBanner.IsVisible = true;
                InsertLog($"发现新版本：v{r.LatestTag}（当前 v{r.CurrentTag}）　下载页：{r.ReleaseUrl}");
            });
        }
        catch
        {
            // 检查更新失败不影响任何功能
        }
    }

    /// <summary>左键点提示条 = 打开下载页；右键点 = 跳过这个版本（不再提示，下个版本仍会提示）。</summary>
    private void UpdateBanner_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (_cfg != null && !string.IsNullOrEmpty(_updateTag))
            {
                _cfg.SkippedUpdateTag = _updateTag;
                _cfg.Save();
                UpdateBanner.IsVisible = false;
                InsertLog($"已跳过 v{_updateTag}；下个新版本仍会提示。");
            }
            return;
        }

        AutoUpdate.OpenUrl(_updateUrl);
        InsertLog($"已打开下载页：{_updateUrl}");
    }

    /// <summary>输入兼容档位：只影响下一次开始播放时的事件时序，不需要刷新预览。</summary>
    private void Timing_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (!_uiReady) return;
        var t = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        InsertLog($"输入兼容档位：{t.Name}（帧长 {t.FrameMs:F0}ms、修饰键提前 {t.ModLeadMs:F0}ms、" +
                  $"同键重触发 {t.RetriggerMs:F0}ms）");
    }

    /// <summary>「播放后自动最小化窗口」：只影响开始播放时是否缩窗，不需要刷新预览。</summary>
    private void AutoMinimize_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
    }

    private void RefreshPreview()
    {
        var okColor = Avalonia.Media.Brushes.SeaGreen;
        var warnColor = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B25A00"));

        var rows = ActiveRows();
        if (rows.Count == 0 || _parsed == null)
        {
            LblMelody.Text = _parsed == null
                ? "打开 MIDI 文件，并在左侧单击一行作为主旋律（可勾选“合”多个声部一起吹）。"
                : "已载入文件 —— 单击一行作为主旋律；勾选“合”可按 1、2、3 优先级把多个声部一起吹。";
            LblWarn.Text = "";
            LblWarn.Foreground = warnColor;
            UpdateTransportUi();
            return;
        }

        var m = BuildMapping();
        if (rows.Count > 1)
        {
            string order = string.Join(" > ", rows.Select(r => $"{r.MixRank}「{r.Name}」"));
            LblMelody.Text = $"合奏 {rows.Count} 个声部（优先级 {order}）：冲突时先吹编号小的";
        }
        else
        {
            var cand = rows[0].Candidate;
            LblMelody.Text = $"主旋律：轨道 {cand.TrackIndex + 1} / 声道 {cand.Channel + 1}「{cand.Name}」" +
                             $"（共 {cand.NoteCount} 音）";
        }

        if (m.InRangeCount == 0)
        {
            LblWarn.Text = m.Notes.Count == 0
                ? "该轨道/声道没有音符，请换一行。"
                : "所有音都超出可演奏音域（低音do~高高音#do），无法演奏——请把“移调”调到 0 附近再试。";
            LblWarn.Foreground = warnColor;
        }
        else if (m.SkipCount > 0)
        {
            LblWarn.Text = $"有 {m.SkipCount} 个音超出可演奏音域（低音do~高高音#do），无法演奏，将自动空拍（可用“移调”调整）。";
            LblWarn.Foreground = warnColor;
        }
        else
        {
            LblWarn.Text = "全部音都在可演奏音域内，可直接演奏。";
            LblWarn.Foreground = okColor;
        }
        UpdateTransportUi();
    }

    // ================= 播放 =================

    private void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
        // ▶ 播放 / ⏸ 暂停 二合一：播放中点它=暂停，暂停中点它=继续
        if (_engine is { IsRunning: true })
        {
            TogglePause();
            return;
        }
        RequestPlay();
    }

    private void RequestPlay()
    {
        if (_busy || ActiveRows().Count == 0) return;

        var map = BuildMapping();
        if (map.InRangeCount == 0)
        {
            InsertLog("没有可演奏的音，无法播放。请调整“移调”或换一行。");
            return;
        }
        _playNotes = map.Notes.Where(n => n.InRange).ToList();

        SetBusy(true);
        int cd = SelectedCountdownSeconds;
        if (cd > 0)
        {
            _countdownLeft = cd;
            UpdateCountdownText();
            InsertLog($"{cd} 秒后自动开始吹奏——请切到游戏窗口并装备口琴…");
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (_, _) =>
            {
                _countdownLeft--;
                if (_countdownLeft <= 0)
                {
                    _countdownTimer!.Stop();
                    _countdownTimer = null;
                    StartPlayback();
                }
                else
                {
                    UpdateCountdownText();
                }
            };
            _countdownTimer.Start();
        }
        else
        {
            StartPlayback();
        }
    }

    private int SelectedCountdownSeconds
    {
        get
        {
            int i = CountdownCombo.SelectedIndex;
            if (i < 0 || i >= CountdownOptions.Length) return 3;
            return CountdownOptions[i];
        }
    }

    private void UpdateCountdownText()
    {
        LblStatus.Foreground = Avalonia.Media.Brushes.Crimson;
        LblStatus.FontSize = 38;   // 切游戏前的最后几秒必须一眼看到
        LblStatus.Text = $"{_countdownLeft} 秒后开始 —— 请切到游戏并装备口琴（再按 F6 可取消）";
        SetCountdownChrome(true);
    }

    /// <summary>倒计时期间窗口底色轻微变暖做提醒，结束时恢复原色。</summary>
    private void SetCountdownChrome(bool on)
    {
        Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(on ? "#FFF3E4D8" : "#F3F4F6"));
    }

    private void StartPlayback()
    {
        if (_playNotes.Count == 0) return;

        if (_removedLeadSec > 0.05)
            InsertLog($"已去除开头空拍 {_removedLeadSec:F1} 秒（“去除开头空拍”勾选生效），旋律将从第 0 秒开始。");

        var engine = new PlaybackEngine();
        _engine = engine;
        engine.Log += s => UiPost(() => InsertLog(s));
        engine.Finished += () => UiPost(OnEngineFinished);

        double speed = SliderSpeed.Value / 100.0;
        bool loop = ChkLoop.IsChecked == true;

        if (!Input.InputSender.IsSupported)
            InsertLog("（当前平台不支持输入模拟，仅流程演示）");

        SliderProgress.Value = 0;
        _gameHwnd = IntPtr.Zero;   // 新一轮播放重新记忆游戏窗口
        bool breath = ChkBreath.IsChecked == true;
        engine.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        engine.Play(_playNotes, speed, FixedLeadMs, loop, breath);
        SliderProgress.Maximum = Math.Max(0.1, engine.TotalSeconds);
        // 播放前自检：把"按键发不进游戏"的常见原因直接指出来，省得用户逐个猜
        RunPreflight();

        string fgTitle = InputSender.ForegroundWindowTitle;
        InsertLog($"开始吹奏；当前前台窗口：{(string.IsNullOrEmpty(fgTitle) ? "（读不到，可能未切到游戏）" : fgTitle)}");
        LblStatus.Foreground = Avalonia.Media.Brushes.SeaGreen;
        LblStatus.FontSize = 21;
        SetCountdownChrome(false);
        LblStatus.Text = "演奏中…";

        // 播放后是否自动最小化：由选项决定（放副屏观察进度时可保持窗口）
        if (ChkAutoMinimize.IsChecked == true)
        {
            if (WindowState != WindowState.Minimized)
                WindowState = WindowState.Minimized;
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;   // 关掉该选项时，若本来缩着就恢复出来
            InsertLog("（未自动最小化窗口：请点一下游戏画面让游戏获得焦点，否则按键会发到本程序窗口）");
        }

        UpdateTransportUi();
        SliderProgress.IsEnabled = true;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += (_, _) =>
        {
            var eng = _engine;
            if (eng == null || !eng.IsRunning) return;

            // 记住“不是本程序”的前台窗口（游戏），供停止时归还焦点用
            IntPtr fg = Input.InputSender.ForegroundWindow;
            if (fg != IntPtr.Zero && fg != SelfHwnd) _gameHwnd = fg;

            if (!_seeking)   // 拖动进度条时不要覆盖用户位置
                SliderProgress.Value = Math.Min(eng.ElapsedSeconds, SliderProgress.Maximum);
            TxtTime.Text = eng.LoopCount > 0
                ? $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s（第 {eng.LoopCount + 1} 遍）"
                : $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s";
            if (!string.IsNullOrEmpty(eng.CurrentNote))
            {
                LblStatus.Foreground = Avalonia.Media.Brushes.SeaGreen;
                LblStatus.Text = eng.CurrentNote;
            }
        };
        _uiTimer.Start();
    }

    private void OnEngineFinished()
    {
        var eng = _engine;
        _engine = null;
        if (eng != null)
        {
            _uiTimer?.Stop();
            _uiTimer = null;
            InsertLog("播放结束。");
            InsertLog(eng.Probe.Summary());
        }
        ResetUi();
    }

    private void BtnStop_Click(object? sender, RoutedEventArgs e) => StopPlaybackNow();

    private void StopPlaybackNow()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;

        var eng = _engine;
        _engine = null;
        eng?.Stop();

        _uiTimer?.Stop();
        _uiTimer = null;
        if (eng != null) InsertLog(eng.Probe.Summary());
        InsertLog("已停止。");
        ForceReleaseKeysForGame();
        ResetUi();
    }

    private void Disclaimer_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border b) b.IsVisible = false;
    }

    private void ResetUi()
    {
        _liveTimer?.Stop();
        _liveTimer = null;
        _liveQueued = false;
        _seeking = false;
        SetBusy(false);
        RefreshPreview();
        SliderProgress.Value = 0;
        SliderProgress.IsEnabled = false;
        TxtTime.Text = "0.0 / 0.0 s";
        LblStatus.FontSize = 21;
        SetCountdownChrome(false);
        SetIdleHint();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // 暂停中允许打开新 MIDI（载入时会自动先停止当前播放）
        BtnOpen.IsEnabled = !busy || (_engine is { IsRunning: true, IsPaused: true });
        TrackList.IsEnabled = !busy;
        ChkLoop.IsEnabled = !busy;
        ChkChordRoot.IsEnabled = !busy;
        ChkBreath.IsEnabled = !busy;
        ChkVocalExtract.IsEnabled = !busy;
        ChkTrimLead.IsEnabled = !busy;
        ChkAutoMinimize.IsEnabled = !busy;
        CountdownCombo.IsEnabled = !busy;
        BtnAutoTranspose.IsEnabled = !busy;
        BtnExport.IsEnabled = !busy && ActiveRows().Count > 0;
        UpdateTransportUi();
        // 速度 / 移调两个滑条：空闲与播放中都可调（播放中实时生效）
        SliderSpeed.IsEnabled = true;
        SliderTranspose.IsEnabled = true;
    }

    // ================= 托盘 =================

    private void SetupTray()
    {
        if (!OperatingSystem.IsWindows() || _quitNow) return;
        if (_tray != null)
        {
            try { _tray.IsVisible = true; } catch { }
            return;
        }
        try
        {
            // 图标从程序内置资源读取（单文件发布时旁边没有 Assets 目录，必须用资源）
            WindowIcon? icon = null;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://HarpAutoPlayer/Assets/app.ico"));
                icon = new WindowIcon(s);
            }
            catch
            {
                // 内置资源缺失时尝试工作目录旁的 Assets/app.ico
                string p = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(p)) icon = new WindowIcon(File.OpenRead(p));
            }

            var menu = new NativeMenu();
            var miShow = new NativeMenuItem { Header = "显示主窗口" };
            miShow.Click += (_, _) => ShowMainWindow();
            var miToggle = new NativeMenuItem { Header = "开始 / 暂停 / 继续（F6）" };
            miToggle.Click += (_, _) => ToggleControl();
            var miStop = new NativeMenuItem { Header = "停止" };
            miStop.Click += (_, _) => StopPlaybackNow();
            var miQuit = new NativeMenuItem { Header = "退出" };
            miQuit.Click += (_, _) => QuitApp();
            menu.Items.Add(miShow);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miToggle);
            menu.Items.Add(miStop);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miQuit);

            _tray = new TrayIcon
            {
                ToolTipText = "口琴自动演奏器",
                Icon = icon,
                Menu = menu,
                IsVisible = true
            };
            InsertLog("托盘图标已创建（如未显示，请看任务栏通知区“上箭头”内）。");
        }
        catch (Exception ex)
        {
            _tray = null;
            InsertLog($"托盘初始化失败：{ex.Message}（稍后自动重试）");
        }
    }

    /// <summary>窗口显示后再确认托盘图标，Windows 偶发注册慢则延迟重试一次。</summary>
    private void EnsureTray()
    {
        SetupTray();
        if (_tray != null && _tray.IsVisible) return;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            SetupTray();
        };
        retry.Start();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApp()
    {
        _quitNow = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveSettings();

        // 点 × = 彻底退出（托盘图标一并移除）
        _countdownTimer?.Stop();
        _liveTimer?.Stop();
        _uiTimer?.Stop();
        _engine?.Stop();
        Input.GlobalHotkeys.Stop();
        _tray?.Dispose();
        _tray = null;
        ForceReleaseKeysForGame();
    }

    /// <summary>
    /// 彻底松开所有按键/鼠标键。停止时焦点在本窗口，第一次松开可能被本窗口接收；
    /// 把焦点还回记住的游戏窗口后再补发一次，确保游戏一定收到“抬起”。
    /// </summary>
    private void ForceReleaseKeysForGame()
    {
        try
        {
            Input.InputSender.ReleaseEverything();
            IntPtr game = _gameHwnd;
            if (game != IntPtr.Zero && game != SelfHwnd)
            {
                Input.InputSender.BringToForeground(game);
                Thread.Sleep(60);
                Input.InputSender.ReleaseEverything();
            }
        }
        catch
        {
            // 释放失败不阻断流程
        }
    }

    private IntPtr SelfHwnd
    {
        get
        {
            var ph = TryGetPlatformHandle();
            return ph?.Handle ?? IntPtr.Zero;
        }
    }
}
