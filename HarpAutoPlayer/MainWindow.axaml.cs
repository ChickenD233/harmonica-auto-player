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

/// <summary>主窗口：选主旋律与播放控制、全局热键、进度跳转、实时变速/移调，托盘后台与设置记忆。</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackRowVM> _tracks = new();
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
    private List<MappedNote> _previewNotes = new();   // 全量音符（含超音域），供卷帘与定位使用
    private double _previewSeconds;                   // 未播放时的定位秒数
    private int _noteCount;                           // 当前谱面音符数（避免每次点击都重算）
    private readonly ScoreEditor _editor = new();     // 手动编辑后的谱面
    private bool _editing;                            // true = 用编辑结果，不再用自动提取
    private bool _helpOn;                             // 卷帘右侧操作说明：默认收起，保持界面干净
    private bool _liveQueued;       // 已排队待应用的实时移调
    private IntPtr _gameHwnd;       // 播放期间记住的游戏窗口（用于停止时把焦点还给它）
    private double _removedLeadSec; // “去除开头空拍”实际剪掉的秒数（本轮）
    private readonly AppConfig _cfg;
    private TrayIcon? _tray;
    private bool _quitNow;

    private static readonly int[] CountdownOptions = { 0, 3, 5, 10 };
    private const int FixedLeadMs = 25;
    private const double SeekStepSeconds = 5;   // 快进/后退热键的步长

    private bool _uiReady;   // 构造期各下拉框初始化会触发 *Changed，此时不应写日志
    private string _updateUrl = "";     // 有新版本时的下载页
    private string _updateTag = "";     // 有新版本时的版本号

    public MainWindow()
    {
        InitializeComponent();

        TrackList.ItemsSource = _tracks;

        // 倒计时下拉：0/3/5/10 秒，默认 3 秒
        CountdownCombo.ItemsSource = new List<string> { "0秒(立即)", "3秒", "5秒", "10秒" };
        CountdownCombo.SelectedIndex = 1;

        // 统一控制热键下拉：F1..F12（默认 F6 = 开始/暂停/继续）
        var fkeys = new List<string> { "无" };
        for (int i = 1; i <= 12; i++) fkeys.Add("F" + i);
        HotkeyControlCombo.ItemsSource = fkeys;
        HotkeyControlCombo.SelectedIndex = 6;   // F6
        HotkeyRewindCombo.ItemsSource = fkeys;
        HotkeyRewindCombo.SelectedIndex = 5;    // F5
        HotkeyForwardCombo.ItemsSource = fkeys;
        HotkeyForwardCombo.SelectedIndex = 7;   // F7

        // 输入兼容档位：决定修饰键与音键之间的物理时间余量
        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;          // 标准


        // —— 记住上次设置 ——
        _cfg = AppConfig.Load();
        CountdownCombo.SelectedIndex = Math.Clamp(_cfg.CountdownIndex, 0, 3);
        HotkeyControlCombo.SelectedIndex = Math.Clamp(_cfg.ControlHotkeyIndex, 0, 12);
        HotkeyRewindCombo.SelectedIndex = Math.Clamp(_cfg.RewindHotkeyIndex, 0, 12);
        HotkeyForwardCombo.SelectedIndex = Math.Clamp(_cfg.ForwardHotkeyIndex, 0, 12);
        SliderSpeed.Value = Math.Clamp(_cfg.Speed, 50, 200);
        SliderTranspose.Value = Math.Clamp(_cfg.Transpose, -10, 10);
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

        // 捕获“已被滑块内部处理”的指针事件，实现任意位置点击/拖动跳转
        SliderProgress.AddHandler(InputElement.PointerPressedEvent, Progress_PointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerMovedEvent, Progress_PointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerReleasedEvent, Progress_PointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        Roll.SeekPreview += OnRollPreview;
        Roll.SeekCommitted += OnRollSeek;
        Roll.SelectionChanged += UpdateEditUi;
        Roll.EditCommitted += OnRollEditCommitted;
        Roll.ViewChanged += OnRollViewChanged;
        ChkSnap.IsCheckedChanged += (_, _) => Roll.SnapEnabled = ChkSnap.IsChecked == true;
        ChkFollow.IsCheckedChanged += (_, _) => Roll.SetFollow(ChkFollow.IsChecked == true);
        KeyDown += OnWindowKeyDown;
        Roll.SnapEnabled = ChkSnap.IsChecked == true;
        Roll.SetFollow(ChkFollow.IsChecked == true);
        if (RollHelp != null) UpdateHelpVisibility();
        SizeChanged += (_, _) => UpdateHelpVisibility();

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

        // 启动即自检，状态行从一开始就有结论
        RunPreflight();

        // 后台静默查更新（不阻塞界面；没网就跳过）
        _ = CheckUpdateAsync();

        InsertLog("欢迎使用 口琴自动演奏器（三角洲行动）");
        InsertLog("用法：打开 MIDI → 点一行作为主旋律 → 按 F6，倒计时内切到游戏并装备口琴。");
        InsertLog("控制热键：F6 = 开始 / 暂停 / 继续（游戏中生效，可改）。");
        if (OperatingSystem.IsWindows())
        {
            SetupTray();
            Opened += (_, _) => EnsureTray();
        }
        Opened += (_, _) => ShowQuickStartOnce();

        InstallDevSnapshot(this);   // 【开发用，可删】设了 HARP_UI_SNAPSHOT 才生效，见 DevUISnapshot.cs
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
        var codes = new List<int>();
        foreach (var combo in new[] { HotkeyControlCombo, HotkeyRewindCombo, HotkeyForwardCombo })
        {
            int code = CodeOf(combo);
            if (code != 0) codes.Add(code);
        }
        Input.GlobalHotkeys.SetActive(codes);
    }

    /// <summary>下拉项 → 虚拟键码。索引 0 = 无。</summary>
    private static int CodeOf(ComboBox combo)
    {
        int idx = Math.Max(0, combo.SelectedIndex);
        return idx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(idx) : 0;
    }

    private void OnGlobalKeyWorker(int code, bool down)
    {
        UiPost(() => HandleGlobalKey(code));
    }

    private void HandleGlobalKey(int code)
    {
        if (code == 0) return;
        if (code == CodeOf(HotkeyControlCombo)) { ToggleControl(); return; }
        if (code == CodeOf(HotkeyRewindCombo)) { SeekRelative(-SeekStepSeconds); return; }
        if (code == CodeOf(HotkeyForwardCombo)) SeekRelative(SeekStepSeconds);
    }

    /// <summary>
    /// 当前播放位置的唯一来源。三个播放器状态各有一套时间，混用就会取到过期的 0：
    /// 试听中取试听时钟，演奏中取引擎，都没有才取"起始位置"。
    /// </summary>
    private double CurrentPosition()
    {
        if (_previewOn) return PreviewNow();
        var eng = _engine;
        if (eng is { IsRunning: true }) return eng.ElapsedSeconds;
        return _previewSeconds;
    }

    /// <summary>当前总时长。与 <see cref="CurrentPosition"/> 必须取同一套状态，否则夹取会错。</summary>
    private double CurrentTotal()
    {
        if (_previewOn) return _previewTotal;
        var eng = _engine;
        if (eng is { IsRunning: true }) return Math.Max(0.001, eng.TotalSeconds);
        return PreviewTotalSeconds;
    }

    /// <summary>快进/后退固定步长：从当前位置相对移动。试听中、演奏中、空闲都可用。</summary>
    private void SeekRelative(double delta)
    {
        double total = CurrentTotal();
        if (total <= 0) return;
        double cur = Math.Clamp(CurrentPosition(), 0, total);
        double target = Math.Clamp(cur + delta, 0, total);
        ApplySeek(target);
        InsertLog($"{(delta < 0 ? "后退" : "前进")} {Math.Abs(delta):F0} 秒："
                  + $"{cur:F1} → {target:F1} s"
                  + (_previewOn ? "（试听）" : _engine is { IsRunning: true } ? "" : "（未播放，只改了起始位置）"));
    }

    /// <summary>统一控制键（默认 F6）：空闲=开始、倒计时中=取消、播放中=暂停、暂停中=继续；按钮与托盘项共用。</summary>
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
            InsertLog("已取消本次开始，可换好歌后再按一次。");
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
            LblStatus.Foreground = OkBrush;
            LblStatus.FontSize = 22;
            LblStatus.Text = "演奏中…";
        }
        else
        {
            eng.Pause();
            LblStatus.Foreground = FailBrush;
            LblStatus.Text = "已暂停 —— 按 F6 或点「▶ 继续」";
        }
        UpdateTransportUi();
    }

    // ================= 状态区 / 按钮提示 =================

    /// <summary>空闲时的状态区提示。</summary>
    private void SetIdleHint()
    {
        LblStatus.Foreground = NeutralBrush;
        LblStatus.FontSize = 15;
        LblStatus.Text = "打开 MIDI 并点选主旋律 → 按 F6 或点 ▶ 播放";
    }

    /// <summary>按状态切换播放按钮与热键提示；停止按钮在倒计时里也可用。</summary>
    private void UpdateTransportUi()
    {
        var eng = _engine;
        // 播放头跟随只在播放中生效，这里统一告知卷帘
        if (Roll != null) Roll.IsPlaying = eng is { IsRunning: true };
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
        if (TxtLastMsg != null) TxtLastMsg.Text = msg;   // 界面只留最近一条
        Persist.LogFile.Append(line);                    // 完整历史仍然落盘
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
        _cfg.RewindHotkeyIndex = Math.Clamp(HotkeyRewindCombo.SelectedIndex, 0, 12);
        _cfg.ForwardHotkeyIndex = Math.Clamp(HotkeyForwardCombo.SelectedIndex, 0, 12);
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
        if (!SliderProgress.IsEnabled) return;
        _seeking = true;
        SeekThumbTo(e);            // 点哪跳到哪（不用先抓滑块）
        PreviewSeekFromSlider();
    }

    private void Progress_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seeking) return;
        SeekThumbTo(e);            // 按住拖动 = 预览位置（不打断播放）
        PreviewSeekFromSlider();
    }

    private void Progress_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        ApplySeek(SliderProgress.Value);
    }

    private void SeekThumbTo(PointerEventArgs e)
    {
        double w = SliderProgress.Bounds.Width;
        if (w <= 0) return;
        double x = e.GetPosition(SliderProgress).X;
        double frac = Math.Clamp(x / w, 0.0, 1.0);
        SliderProgress.Value = frac * SliderProgress.Maximum;
    }

    /// <summary>拖动进度条时只更新显示，松手才跳转。</summary>
    private void PreviewSeekFromSlider() => ShowPosition(SliderProgress.Value);

    /// <summary>卷帘拖动中：只挪指针与音符显示，不打断播放。</summary>
    private void OnRollPreview(double seconds)
    {
        _seeking = true;          // 让 UI 定时器别把指针拽回去
        ShowPosition(seconds);
    }

    /// <summary>卷帘松手：真正跳转。</summary>
    private void OnRollSeek(double seconds)
    {
        _seeking = false;
        ApplySeek(seconds);
    }

    /// <summary>定位到某个秒数：试听中跳转试听，演奏中跳转引擎，都没有只记住位置。</summary>
    private void ApplySeek(double seconds)
    {
        if (_previewOn)
        {
            PreviewSeekTo(seconds);
            ShowPosition(seconds);
            return;
        }

        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            double total = Math.Max(0.001, eng.TotalSeconds);
            eng.SeekFraction(Math.Clamp(seconds / total, 0, 1));
        }
        else
        {
            _previewSeconds = Math.Clamp(seconds, 0, PreviewTotalSeconds);
        }
        ShowPosition(seconds);
    }

    private double PreviewTotalSeconds =>
        _previewNotes.Count == 0 ? 0 : _previewNotes.Max(n => n.End);

    /// <summary>进度条/卷帘/时间/定位音一起摆到某个秒数（不动引擎，也不动试听时钟）。</summary>
    private void ShowPosition(double seconds)
    {
        double total = CurrentTotal();
        double t = Math.Clamp(seconds, 0, total);
        SliderProgress.Value = t;
        Roll.SetPosition(t);
        TxtTime.Text = $"{t:F1} / {total:F1} s";
        UpdateSeekNote(t);
        // 空闲时把位置记下来。否则 F5/F7 取到过期的 0，表现就是"F7 跳到 5 秒、F5 回开头"。
        if (!_previewOn && _engine is not { IsRunning: true }) _previewSeconds = t;
    }

    /// <summary>显示某个时刻的音：音名 + 简谱 + 要按的键。不传则取当前指针位置。</summary>
    private void UpdateSeekNote(double? atSeconds = null)
    {
        if (TxtSeekNote == null) return;
        double t = atSeconds ?? (_engine is { IsRunning: true } ? _engine.ElapsedSeconds : _previewSeconds);
        MappedNote? note = _previewNotes.LastOrDefault(n => n.Start <= t && t < n.End);
        if (note == null) { TxtSeekNote.Text = "—"; return; }
        string name = Music.NoteName(note.Pitch);
        if (!note.InRange) { TxtSeekNote.Text = $"{name} 超音域"; return; }
        string mods = note.OctaveSlot switch
        {
            Slot.Low => "左键+",
            Slot.High => "右键+",
            _ => ""
        };
        if (note.Sharp) mods += "中键+";
        TxtSeekNote.Text = $"{name} {Music.DegreeName(note.Pitch)} · {mods}{note.Key}";
    }

    // ================= 卷帘编辑 =================

    /// <summary>谱面来源要变了：有手动改动就先丢弃并说明，否则用户会以为点了没反应。</summary>
    private void DropEditsIfAny(string why)
    {
        if (!_editing) return;
        ResetEdits();
        InsertLog($"已丢弃手动改动（{why}）。");
    }

    /// <summary>第一次编辑时，把当前自动结果冻结成可编辑谱面。</summary>
    private void BeginEditIfNeeded()
    {
        if (_editing) return;
        _editor.Reset(ComputeAutoNotes());
        _editing = true;
        InsertLog("已进入编辑模式：自动提取的选项不再影响谱面，点「还原为自动」可退出。");
    }

    /// <summary>换歌或点「还原为自动」时丢弃全部手动改动。</summary>
    private void ResetEdits()
    {
        _editing = false;
        _editor.Clear();
        Roll.ClearSelection();
    }

    /// <summary>
    /// 卷帘完成一次编辑手势，提交的是**整条新谱面**（而不是单个音的增量）。
    /// 这样拖动一组音在撤销栈里只算一步；卷帘自己已经持有这份数据，
    /// 所以这里不再把音符推回给它（推回去会重置视口与选择）。
    /// </summary>
    private void OnRollEditCommitted(IReadOnlyList<RawNote> notes, string what)
    {
        BeginEditIfNeeded();
        _editor.ReplaceAll(notes);
        RefreshPreview(pushToRoll: false);
        InsertLog($"已{what}。");
    }

    /// <summary>卷帘视口/缩放/跟随变化：刷新工具栏读数。</summary>
    private void OnRollViewChanged()
    {
        if (TxtZoom == null) return;
        TxtZoom.Text = $"{Roll.ZoomPercent:F0}%";
        if (ChkFollow != null && ChkFollow.IsChecked != Roll.FollowPlayhead)
            ChkFollow.IsChecked = Roll.FollowPlayhead;
    }

    /// <summary>加音用的默认长度：取现有音符的中位长度，夹在 0.1-1.0 秒。</summary>
    private double MedianNoteLength()
    {
        var src = _editing ? _editor.Notes : ComputeAutoNotes();
        var lens = src.Select(n => n.End - n.Start).Where(l => l > 0.02).OrderBy(l => l).ToList();
        if (lens.Count == 0) return 0.25;
        return Math.Clamp(lens[lens.Count / 2], 0.1, 1.0);
    }

    private void DoUndo()
    {
        if (!_editing || !_editor.Undo()) { InsertLog("没有可撤销的操作。"); return; }
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已撤销。");
    }

    private void DoRedo()
    {
        if (!_editing || !_editor.Redo()) { InsertLog("没有可重做的操作。"); return; }
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已重做。");
    }

    /// <summary>删除卷帘里选中的音（工具栏按钮 / Delete 键）。</summary>
    private void DeleteSelectedNote()
    {
        if (!Roll.HasSelection) { InsertLog("先在卷帘上点一个音（或框选几个），再删除。"); return; }
        Roll.DeleteSelected();
    }

    private void UpdateEditUi()
    {
        if (BtnUndo == null) return;
        BtnUndo.IsEnabled = _editing && _editor.CanUndo;
        BtnRedo.IsEnabled = _editing && _editor.CanRedo;
        BtnDeleteNote.IsEnabled = Roll.HasSelection;
        BtnResetEdits.IsEnabled = _editing;
        BtnExportMidi.IsEnabled = _noteCount > 0;
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => DoUndo();
    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => Roll.ZoomCenter(0.6);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => Roll.ZoomCenter(1.67);
    private void ZoomFit_Click(object? sender, RoutedEventArgs e) => Roll.FitAll();
    private void Redo_Click(object? sender, RoutedEventArgs e) => DoRedo();
    private void DeleteNote_Click(object? sender, RoutedEventArgs e) => DeleteSelectedNote();

    private void Snap_Changed(object? sender, RoutedEventArgs e)
    {
        if (Roll != null) Roll.SnapEnabled = ChkSnap.IsChecked == true;
    }

    /// <summary>帮助按钮：显示 / 收起卷帘右侧的操作说明。</summary>
    private void Help_Click(object? sender, RoutedEventArgs e)
    {
        _helpOn = !_helpOn;
        UpdateHelpVisibility();
    }

    /// <summary>
    /// 说明栏占 188px。窗口太窄时，右栏减去它就不够放卷帘工具栏，缩放按钮会被挤掉 ——
    /// 所以按窗口宽度自动收起，拉宽后自动恢复。
    /// </summary>
    private void UpdateHelpVisibility()
    {
        if (RollHelp == null) return;
        bool roomy = Bounds.Width >= 1080;
        RollHelp.IsVisible = _helpOn && roomy;
        if (BtnHelp != null)
        {
            BtnHelp.IsEnabled = roomy;
            ToolTip.SetTip(BtnHelp, roomy
                ? (_helpOn ? "收起操作说明，把宽度让给卷帘" : "显示操作说明")
                : "窗口太窄：说明已自动收起，避免把缩放按钮挤掉。把窗口拉宽就会恢复。");
        }
    }

    // ================= 内置试听 =================
    //
    // 试听是一个**独立按钮**，和演奏完全无关：不发按键、不走倒计时、不最小化窗口。
    // 关键在于它是"事件调度"而不是"按固定间隔采样"：
    // 播放前先把每个音的 note-on / note-off 时刻排成一张表（按真实秒），
    // 定时器每次醒来把**所有到点的**事件一次发完。这样即使定时器被 UI 卡住、
    // 或者某个音短于定时器间隔，也不会被漏掉 —— 采样式实现会成片吞音。

    private MidiPreview? _preview;
    private DispatcherTimer? _previewTimer;
    private readonly List<(double T, int Pitch, bool Down)> _previewEvents = new();
    /// <summary>每个音在"真实秒"下的起止，用于跳转时判断"跳进了哪个音的中间"。</summary>
    private readonly List<(double S, double E, int Pitch)> _previewSpans = new();
    private double _previewTotal;
    private int _previewNext;
    /// <summary>试听位置的时间基准：位置 = (现在 - 基准) / 频率。跳转只需挪这个基准。</summary>
    private long _previewBaseTicks;
    private bool _previewOn;
    private readonly HashSet<int> _previewSounding = new();

    /// <summary>试听当前所在秒数（真实秒，已含速度）。</summary>
    private double PreviewNow() =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - _previewBaseTicks)
        / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// 试听中跳转到某个秒数：挪时间基准、放开所有在响的音、把事件游标移到该点之后，
    /// 并且把"跳进去的那个音"补上（否则从音符中间跳过去这段就是哑的）。
    /// </summary>
    private void PreviewSeekTo(double seconds)
    {
        double t = Math.Clamp(seconds, 0, _previewTotal);
        _previewBaseTicks = System.Diagnostics.Stopwatch.GetTimestamp()
                            - (long)(t * System.Diagnostics.Stopwatch.Frequency);

        _preview?.StopAll();
        _previewSounding.Clear();

        _previewNext = 0;
        while (_previewNext < _previewEvents.Count && _previewEvents[_previewNext].T < t) _previewNext++;

        if (_preview != null)
        {
            foreach (var s in _previewSpans)
            {
                if (s.S <= t && t < s.E)
                {
                    _preview.PlayNote(s.Pitch, velocity: 96, autoRelease: false);
                    _previewSounding.Add(s.Pitch);
                }
            }
        }
    }

    /// <summary>试听按钮：点一下开始放声音，再点一下停止。</summary>
    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (_previewOn) StopPreviewAudio();
        else StartPreviewAudio();
    }

    private void StartPreviewAudio()
    {
        // 不能用 _playNotes：那个只在"开始播放"时才填。用户刚打开文件就点试听时它是空的。
        if (_busy || _engine is { IsRunning: true })
        {
            InsertLog("试听与演奏不能同时进行，先停止当前演奏。");
            return;
        }
        var notes = BuildMapping().Notes.Where(n => n.InRange).ToList();
        if (notes.Count == 0)
        {
            InsertLog("当前谱面没有可演奏的音，无法试听。先选一行主旋律。");
            return;
        }

        if (_preview == null)
        {
            _preview = new MidiPreview();
            if (!_preview.IsAvailable)
            {
                InsertLog($"试听不可用：{_preview.LastError}。" +
                          "系统可能没有可用的 MIDI 输出设备（正常应有 Microsoft GS Wavetable Synth）。");
                _preview.Dispose();
                _preview = null;
                BtnPreview.IsEnabled = false;
                return;
            }
        }

        // 与演奏同一套时间基准：谱面时间除以速度 = 真实秒
        double speed = Math.Max(0.1, SliderSpeed.Value / 100.0);
        const double gap = 0.02;   // 同音高重复时留出断开，否则不会重新触发
        _previewEvents.Clear();
        _previewSpans.Clear();
        foreach (var n in notes)
        {
            double s = n.Start / speed;
            double e = n.End / speed;
            double off = Math.Max(s + 0.03, e - gap);
            _previewEvents.Add((s, n.Pitch, true));
            _previewEvents.Add((off, n.Pitch, false));
            _previewSpans.Add((s, off, n.Pitch));
        }
        _previewEvents.Sort((a, b) => a.T.CompareTo(b.T));

        _previewNext = 0;
        _previewSounding.Clear();
        _previewTotal = _previewEvents.Count == 0 ? 0 : _previewEvents[^1].T;
        // 从进度条当前位置开始试听（用户可能已经把指针拖到某处了）
        double startAt = Math.Clamp(SliderProgress.Value, 0, _previewTotal);
        _previewBaseTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _previewOn = true;
        BtnPreview.Content = "⏹ 停止试听";
        SliderProgress.Maximum = Math.Max(0.1, _previewTotal);
        PreviewSeekTo(startAt);

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _previewTimer.Tick += (_, _) => PreviewTick();
        _previewTimer.Start();

        InsertLog($"试听开始：{notes.Count} 个音，约 {_previewTotal:F1}s（不发送按键）");
    }

    private void PreviewTick()
    {
        if (!_previewOn || _preview == null) return;

        // 用户正在拖进度条/卷帘：这一帧不推进也不覆盖，把画面交给拖动
        if (_seeking) return;

        double t = PreviewNow();

        // 一次补齐所有到点的事件（不是只看"当前这一刻"，所以不会漏音）
        while (_previewNext < _previewEvents.Count && _previewEvents[_previewNext].T <= t)
        {
            var e = _previewEvents[_previewNext++];
            if (e.Down)
            {
                _preview.PlayNote(e.Pitch, velocity: 96, autoRelease: false);
                _previewSounding.Add(e.Pitch);
            }
            else
            {
                _preview.StopNote(e.Pitch);
                _previewSounding.Remove(e.Pitch);
            }
        }

        double shown = Math.Min(t, _previewTotal);
        if (_previewTotal > 0)
        {
            SliderProgress.Value = shown;
            TxtTime.Text = $"{shown:F1} / {_previewTotal:F1} s";
            Roll.SetPosition(shown);
            UpdateSeekNote(shown);
        }

        if (_previewNext >= _previewEvents.Count) StopPreviewAudio();
    }

    /// <summary>停止试听：放掉所有正在响的音，恢复按钮文字。</summary>
    private void StopPreviewAudio()
    {
        if (!_previewOn && _previewTimer == null) return;
        _previewOn = false;
        _previewTimer?.Stop();
        _previewTimer = null;
        _previewSounding.Clear();
        _preview?.StopAll();
        if (BtnPreview != null) BtnPreview.Content = "试听";
    }

    private void ResetEdits_Click(object? sender, RoutedEventArgs e)
    {
        if (!_editing) { InsertLog("当前就是自动结果，没有可还原的改动。"); return; }
        ResetEdits();
        RefreshPreview();
        InsertLog("已还原为自动提取结果。");
    }

    /// <summary>窗口级快捷键：删除、撤销、重做。卷帘不必先获得焦点。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) DoRedo(); else DoUndo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteSelectedNote(); e.Handled = true; }
    }

    /// <summary>把当前谱面写成标准 MIDI 文件。</summary>
    private async void ExportMidi_Click(object? sender, RoutedEventArgs e)
    {
        var raw = GetActiveRawNotes();
        if (raw.Count == 0) { InsertLog("没有音符可导出。"); return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出编辑后的 MIDI",
                SuggestedFileName = SuggestMidiName(),
                DefaultExtension = "mid",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("MIDI 文件") { Patterns = new List<string> { "*.mid" } }
                }
            });
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            MidiExporter.Write(path, raw, "HarpAutoPlayer 编辑");
            InsertLog($"已导出 MIDI：{raw.Count} 个音 → {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            InsertLog($"导出 MIDI 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string SuggestMidiName()
    {
        string name = "edited";
        if (_parsed != null && !string.IsNullOrEmpty(_parsed.FilePath))
            name = System.IO.Path.GetFileNameWithoutExtension(_parsed.FilePath) + "-edited";
        return name + ".mid";
    }

    /// <summary>没有可定位的谱面时，清空进度条、卷帘与音符显示。</summary>
    private void ResetSeekUi()
    {
        Roll.SetNotes(Array.Empty<RawNote>(), Array.Empty<int>(), 0);
        Roll.SetPosition(0);
        SliderProgress.Maximum = 0.1;
        SliderProgress.Value = 0;
        SliderProgress.IsEnabled = false;
        TxtTime.Text = "0.0 / 0.0 s";
        TxtSeekNote.Text = "—";
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

            // 暂停/播放中也能换歌：先停掉当前播放，避免新旧串曲
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
                _previewSeconds = 0;      // 换歌必须回到 0，否则上一首的位置会夹到新曲末尾 → 一播放就结束
                Roll.FitAll();
                ResetEdits();
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

    /// <summary>换歌前停掉旧曲（松开按键、释放引擎）。</summary>
    private void StopPlaybackForNewFile()
    {
        if (_engine is not { IsRunning: true }) return;
        StopPlaybackNow();
        InsertLog("已停止当前播放（换歌）。");
    }

    // ================= 主旋律选择 =================

    /// <summary>载入后自动挑最像主旋律的轨并选中。依据：非打击乐、轨名像旋律、音域贴合口琴。</summary>
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
            InsertLog("没有适合口琴的旋律轨（全是打击乐？），请手动点选一行。");
            return;
        }

        best.IsRecommended = true;
        TrackList.SelectedItem = best;   // 触发 SelectionChanged → SetMain → 高亮
        if (bestScore >= 20)
            InsertLog($"已自动选中推荐轨：{best.DisplayName}（想换就点其它行）");
        else
            InsertLog($"已自动选中较合适的轨：{best.DisplayName}（音域贴合不多，可用「一键移调」）");

        // 覆盖不全就明说：进度条与卷帘只覆盖这一段，免得用户以为「加载不全」
        double span = best.Candidate.Notes.Count == 0 ? 0 : best.Candidate.Notes.Max(n => n.End);
        double fileSec = _parsed?.DurationSec ?? 0;
        if (fileSec > 5 && span < fileSec * 0.6)
            InsertLog($"注意：这条轨只到 {span:F1}s，全曲 {fileSec:F1}s。" +
                      $"进度条与卷帘只覆盖这一段，可在左侧点其它行换轨。");
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

            // 覆盖时长：只盖住开头几秒的轨（前奏、过门、演示音）不该压过整首主旋律。
            // 用「最后一个音的结束时刻」而不是跨度，这样后半段才进旋律的轨也能得高分。
            double fileSec = _parsed?.DurationSec ?? 0;
            if (fileSec > 1)
            {
                double cover = Math.Clamp(notes.Max(n => n.End) / fileSec, 0, 1);
                s += 60.0 * cover * cover;
                if (cover < 0.25) s -= 25;
            }
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
        DropEditsIfAny("换了主旋律轨");
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

    /// <summary>当前谱面：手动编辑过就用编辑结果，否则用自动提取结果。</summary>
    private List<RawNote> GetActiveRawNotes() =>
        _editing ? _editor.Notes.ToList() : ComputeAutoNotes();

    /// <summary>
    /// 当前要演奏的音符（未移调）：把勾选的声部按优先级合并成一条单音线。
    /// 「自动提取主旋律 / 人声旋律提取」已移除 —— 它们是黑盒猜测，猜错时用户无从下手；
    /// 现在卷帘可以直接看、直接改，比猜得准。
    /// </summary>
    private List<RawNote> ComputeAutoNotes()
    {
        var rows = ActiveRows();
        if (rows.Count == 0)
        {
            _removedLeadSec = 0;
            return new List<RawNote>();
        }
        var voices = new List<(int Rank, RawNote Note)>();
        for (int k = 0; k < rows.Count; k++)
            foreach (var n in rows[k].Candidate.Notes) voices.Add((k + 1, n));  // 1 最优先
        var merged = NoteMapper.MergeVoicesByPriority(voices);

        // 「去除开头空拍」：整条旋律平移到第一个音从 0 秒开始（开头常有休止）
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

    private void ExportGhub_Click(object? sender, RoutedEventArgs e)
        => ExportSchedule(MacroExporter.Format.LogitechGHub);

    private void ExportCsv_Click(object? sender, RoutedEventArgs e)
        => ExportSchedule(MacroExporter.Format.KeystrokeCsv);

    private async void ExportSchedule(MacroExporter.Format format)
    {
        try
        {
            var map = BuildMapping();
            var playable = map.Notes.Where(n => n.InRange).ToList();
            if (playable.Count == 0)
            {
                InsertLog("没有可演奏的音，无法导出。请调整「移调」或换一行。");
                return;
            }

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

            InsertLog($"已导出按键表：{playable.Count} 音 / {nEvents} 事件 / {seconds:F1}s" +
                      $"（速度 {speed * 100:F0}%、档位 {timing.Name}）");
            InsertLog($"　文件：{outPath}");
            if (format == MacroExporter.Format.LogitechGHub)
                InsertLog("　G HUB 用法：设备 → 游戏与应用程序 → 添加游戏 → 编写脚本 → 整段粘贴保存。");
            else
                InsertLog("　提示：雷蛇 Synapse 宏是私有格式，请用其宏录制功能代替。");
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
            InsertLog($"一键移调：整体 {sign}{best} 半音后全部音在音域内。");
        else
            InsertLog($"一键移调：整体 {sign}{best} 半音后仍剩 {bestSkip} 个音超音域（跨度过大，仍会空拍）");
        RefreshPreview();
    }

    /// <summary>勾选“合”：勾选先后即主次（先勾=1 主）；勾完立即刷新，可直接播放。</summary>
    private void Mix_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TrackRowVM row)
        {
            DropEditsIfAny("改了合奏声部");
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

    // ================= 播放前自检 =================

    /// <summary>播放前自检管理员权限与前台输入法，结果显示在界面状态行（✔ / ✘），不写日志。</summary>
    private void RunPreflight()
    {
        PreflightCheck.Report report;
        try
        {
            report = PreflightCheck.Run();
        }
        catch
        {
            // 检测失败就不显示结论，避免给出误导性的 ✘
            TxtCheckAdminMark.Text = "–";
            TxtCheckAdminMark.Foreground = NeutralBrush;
            TxtCheckImeMark.Text = "–";
            TxtCheckImeMark.Foreground = NeutralBrush;
            TxtCheckHint.Text = "";
            return;
        }

        PaintCheck(report.Admin, TxtCheckAdminMark, TxtCheckAdmin);
        PaintCheck(report.Ime, TxtCheckImeMark, TxtCheckIme);

        TxtCheckHint.Text = report.AllPassed
            ? ""
            : string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => !c.Passed)
                .Select(c => c.Detail));
    }

    /// <summary>手动刷新自检（切换输入法后点一下即可）。</summary>
    private void BtnRecheck_Click(object? sender, RoutedEventArgs e)
    {
        RunPreflight();
        InsertLog("已重新检测管理员权限与输入法。");
    }

    // 与 Styles/Theme.axaml 的语义色 token 保持一致（改配色时两处一起改）
    private static readonly Avalonia.Media.IBrush OkBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E7A3C"));   // = BrushSuccess
    private static readonly Avalonia.Media.IBrush FailBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0392B"));   // = BrushDanger
    private static readonly Avalonia.Media.IBrush NeutralBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8A93A0"));   // = BrushTextMuted

    /// <summary>通过打勾、未通过打叉。</summary>
    private static void PaintCheck(PreflightCheck.Check c,
                                   Avalonia.Controls.TextBlock mark,
                                   Avalonia.Controls.TextBlock label)
    {
        mark.Text = c.Passed ? "✔" : "✘";
        mark.Foreground = c.Passed ? OkBrush : FailBrush;
        label.Foreground = c.Passed ? OkBrush : FailBrush;
        label.Text = $"{c.Name}：{c.Detail}";
    }

    // ================= 自动检查更新 =================

    /// <summary>后台查询 GitHub 最新 Release，有新版则顶部显示提示条；没网/被墙静默忽略。</summary>
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
                                 "点此打开下载页。";
                UpdateBanner.IsVisible = true;
                InsertLog($"发现新版本：v{r.LatestTag}（当前 v{r.CurrentTag}）　下载页：{r.ReleaseUrl}");
            });
        }
        catch
        {
            // 检查更新失败不影响任何功能
        }
    }

    /// <summary>左键点提示条 = 打开下载页；右键点 = 跳过本版本（下个版本仍会提示）。</summary>
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
        InsertLog($"输入兼容档位：{t.Name}（帧 {t.FrameMs:F0}ms、修饰键提前 {t.ModLeadMs:F0}ms、" +
                  $"重触发 {t.RetriggerMs:F0}ms）");
    }

    /// <summary>「播放后自动最小化窗口」：只影响开始播放时是否缩窗，不需要刷新预览。</summary>
    private void AutoMinimize_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
    }

    /// <summary>刷新需选中声轨才能用的按钮（一键移调、导出），播放中也能导出。</summary>
    private void UpdateActionButtons()
    {
        bool hasRows = ActiveRows().Count > 0;
        BtnAutoTranspose.IsEnabled = hasRows;
        BtnExport.IsEnabled = hasRows;
    }

    /// <summary>刷新旋律预览与提示文案（载入文件、切换声轨、改选项后调用）。</summary>
    /// <param name="pushToRoll">
    /// true = 把谱面推回卷帘（换歌/换轨/改选项，会重置它的视口与选择）；
    /// false = 卷帘自己刚提交的编辑，数据已在它手里，不要再推回去。
    /// </param>
    /// <param name="keepView">
    /// 推回谱面时是否保留卷帘当前的缩放与位置。撤销/重做必须为 true ——
    /// 否则用户放大到某一段改谱，一按 Ctrl+Z 就被弹回全曲，没法连续编辑。
    /// </param>
    private void RefreshPreview(bool pushToRoll = true, bool keepView = false)
    {
        UpdateActionButtons();

        // 空状态引导：没有轨道时显示提示，别留一大片空白
        if (EmptyHint != null) EmptyHint.IsVisible = _tracks.Count == 0;

        var okColor = OkBrush;
        var warnColor = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B25A00"));

        var rows = ActiveRows();
        if (rows.Count == 0 || _parsed == null)
        {
            LblMelody.Text = _parsed == null
                ? "打开 MIDI 文件并单击一行作为主旋律（可勾选“合”多声部一起吹）。"
                : "已载入 —— 单击一行作为主旋律；勾选“合”可按 1、2、3 优先级合奏。";
            LblWarn.Text = "";
            LblWarn.Foreground = warnColor;
            _previewNotes = new List<MappedNote>();
            _previewSeconds = 0;
            ResetSeekUi();
            UpdateEditUi();
            UpdateTransportUi();
            return;
        }

        var raw = GetActiveRawNotes();
        var m = raw.Count == 0 ? new MappingResult() : NoteMapper.Map(raw, CurrentTranspose, null);

        // 载入后即可定位：进度条与卷帘按谱面时间摆好（不必先播放）
        _previewNotes = m.Notes;
        double totalSec = PreviewTotalSeconds;
        _previewSeconds = Math.Clamp(_previewSeconds, 0, totalSec);
        // 卷帘轴上留 2% 余量，末尾才好双击加音
        _noteCount = raw.Count;
        // 绿=可演奏、灰=超出音域，由这份音高集合决定。
        // 两条分支都必须更新它：编辑路径不经过 SetNotes，否则改完音高颜色会按旧集合算。
        var inRangePitches = m.Notes.Where(n => n.InRange).Select(n => n.Pitch).Distinct().ToList();
        if (pushToRoll)
        {
            Roll.SetTempo(_parsed.SecondsPerBeat, _parsed.BeatsPerBar);
            Roll.DefaultNoteSeconds = MedianNoteLength();
            Roll.SetNotes(raw, inRangePitches, totalSec * 1.02 + 0.3, preserveView: keepView);
            Roll.SetTrimInfo(_removedLeadSec);
            Roll.SetPosition(_previewSeconds);
            OnRollViewChanged();
        }
        else
        {
            // 编辑提交：只同步进度条、音高颜色与读数，卷帘保持自己的视口与选择
            Roll.SetInRangePitches(inRangePitches);
            SliderProgress.IsEnabled = m.Notes.Count > 0;
            UpdateSeekNote();
        }
        SliderProgress.Maximum = Math.Max(0.1, totalSec);
        SliderProgress.Value = _previewSeconds;
        SliderProgress.IsEnabled = m.Notes.Count > 0;
        TxtTime.Text = $"{_previewSeconds:F1} / {totalSec:F1} s";
        UpdateSeekNote();
        UpdateEditUi();

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
                : "所有音都超出音域（低音do~高高音#do），请把“移调”调到 0 附近再试。";
            LblWarn.Foreground = warnColor;
        }
        else if (m.SkipCount > 0)
        {
            LblWarn.Text = $"有 {m.SkipCount} 个音超出音域（低音do~高高音#do），将自动空拍（可用“移调”调整）。";
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
            InsertLog($"{cd} 秒后开始——请切到游戏窗口并装备口琴…");
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
        LblStatus.Foreground = FailBrush;
        LblStatus.FontSize = 38;   // 切游戏前的最后几秒必须一眼看到
        LblStatus.Text = $"{_countdownLeft} 秒后开始 —— 请切到游戏并装备口琴（F6 可取消）";
        SetCountdownChrome(true);
    }

    /// <summary>倒计时期间窗口底色轻微变暖做提醒，结束时恢复原色。</summary>
    private void SetCountdownChrome(bool on)
    {
        Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(on ? "#FFF3E4D8" : "#F4F6F9"));   // 暖色提醒 / 常态底色（= BrushCanvas）
    }

    private void StartPlayback()
    {
        if (_playNotes.Count == 0) return;

        if (_removedLeadSec > 0.05)
            InsertLog($"已去除开头空拍 {_removedLeadSec:F1} 秒，旋律从第 0 秒开始。");

        var engine = new PlaybackEngine();
        _engine = engine;
        engine.Log += s => UiPost(() => InsertLog(s));
        engine.Finished += () => UiPost(OnEngineFinished);

        double speed = SliderSpeed.Value / 100.0;
        bool loop = ChkLoop.IsChecked == true;

        if (!Input.InputSender.IsSupported)
            InsertLog("（当前平台不支持输入模拟，仅流程演示）");

        // 播放前可能已把进度条或卷帘拖到某个位置，从那里开始
        double startFrac = SliderProgress.Maximum > 0
            ? Math.Clamp(SliderProgress.Value / SliderProgress.Maximum, 0, 1) : 0;
        _gameHwnd = IntPtr.Zero;   // 新一轮播放重新记忆游戏窗口
        engine.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        engine.Play(_playNotes, speed, FixedLeadMs, loop);
        SliderProgress.Maximum = Math.Max(0.1, engine.TotalSeconds);
        if (startFrac > 0.0005)
        {
            engine.SeekFraction(startFrac);
            SliderProgress.Value = startFrac * SliderProgress.Maximum;
            InsertLog($"从 {startFrac * engine.TotalSeconds:F1} 秒开始播放。");
        }
        // 自检把“按键发不进游戏”的常见原因指出来，省得用户逐个猜
        RunPreflight();

        string fgTitle = InputSender.ForegroundWindowTitle;
        InsertLog($"开始吹奏；前台窗口：{(string.IsNullOrEmpty(fgTitle) ? "（读不到，可能未切到游戏）" : fgTitle)}");
        LblStatus.Foreground = OkBrush;
        LblStatus.FontSize = 22;
        SetCountdownChrome(false);
        LblStatus.Text = "演奏中…";

        // 是否自动最小化由选项决定（副屏看进度时可保持窗口）
        if (ChkAutoMinimize.IsChecked == true)
        {
            if (WindowState != WindowState.Minimized)
                WindowState = WindowState.Minimized;
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;   // 关掉该选项时，若本来缩着就恢复出来
            InsertLog("（未自动最小化：请点一下游戏画面让它获得焦点，否则按键发到本窗口）");
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

            if (!_seeking)   // 拖动进度条或卷帘时不要覆盖用户位置
            {
                SliderProgress.Value = Math.Min(eng.ElapsedSeconds, SliderProgress.Maximum);
                TxtTime.Text = eng.LoopCount > 0
                    ? $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s（第 {eng.LoopCount + 1} 遍）"
                    : $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s";
                Roll.SetPosition(eng.ElapsedSeconds);
                UpdateSeekNote();
            }
            if (!string.IsNullOrEmpty(eng.CurrentNote))
            {
                LblStatus.Foreground = OkBrush;
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
        StopPreviewAudio();

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
        // 停止后按谱面时间换算当前位置，方便直接重新定位
        double frac = SliderProgress.Maximum > 0 ? SliderProgress.Value / SliderProgress.Maximum : 0;
        _previewSeconds = frac * PreviewTotalSeconds;
        RefreshPreview();
        Roll.SetPosition(_previewSeconds);
        LblStatus.FontSize = 22;
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
        ChkTrimLead.IsEnabled = !busy;
        ChkAutoMinimize.IsEnabled = !busy;
        BtnPreview.IsEnabled = !busy;
        CountdownCombo.IsEnabled = !busy;
        // 一键移调 / 导出的可用性统一由 UpdateActionButtons() 决定，这里不再覆盖。
        UpdateActionButtons();

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
            // 图标必须走内置资源：单文件发布时旁边没有 Assets 目录
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
            InsertLog("托盘图标已创建（如未显示，看任务栏通知区“上箭头”内）");
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
        StopPreviewAudio();
        _preview?.Dispose();
        _preview = null;
        Input.GlobalHotkeys.Stop();
        _tray?.Dispose();
        _tray = null;
        ForceReleaseKeysForGame();
    }

    /// <summary>彻底松开按键/鼠标键：停止时焦点在本窗口，先把焦点还给记住的游戏窗口再补发一次。</summary>
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
