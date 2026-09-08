using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HarpAutoPlayer.Engine;
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
    private readonly AppConfig _cfg;
    private TrayIcon? _tray;
    private bool _quitNow;

    private static readonly int[] CountdownOptions = { 0, 3, 5, 10 };
    private const int FixedLeadMs = 25;

    public MainWindow()
    {
        InitializeComponent();

        TrackList.ItemsSource = _tracks;
        LogList.ItemsSource = _log;

        // 倒计时下拉：0/3/5/10 秒，默认 3 秒
        CountdownCombo.ItemsSource = new List<string> { "0秒(立即)", "3秒", "5秒", "10秒" };
        CountdownCombo.SelectedIndex = 1;

        // 全局热键下拉：F1..F12（默认 开始=F5、暂停/继续=F6）
        var fkeys = new List<string> { "无" };
        for (int i = 1; i <= 12; i++) fkeys.Add("F" + i);
        HotkeyStartCombo.ItemsSource = fkeys;
        HotkeyStartCombo.SelectedIndex = 5;   // F5
        HotkeyPauseCombo.ItemsSource = fkeys;
        HotkeyPauseCombo.SelectedIndex = 6;   // F6

        // —— 记住上次设置 ——
        _cfg = AppConfig.Load();
        CountdownCombo.SelectedIndex = Math.Clamp(_cfg.CountdownIndex, 0, 3);
        HotkeyStartCombo.SelectedIndex = Math.Clamp(_cfg.StartHotkeyIndex, 0, 12);
        HotkeyPauseCombo.SelectedIndex = Math.Clamp(_cfg.PauseHotkeyIndex, 0, 12);
        SliderSpeed.Value = Math.Clamp(_cfg.Speed, 50, 200);
        SliderTranspose.Value = Math.Clamp(_cfg.Transpose, -10, 10);
        ChkChordRoot.IsChecked = _cfg.ChordRoot;
        ChkBreath.IsChecked = _cfg.Breath;

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

        InsertLog("欢迎使用 口琴自动演奏器（三角洲行动）");
        InsertLog("用法：打开 MIDI 文件 → 在左侧单击一行作为主旋律 → 点「播放」（或按开始热键 F5），倒计时内切到游戏并装备口琴即可。");
        InsertLog("热键：F5=开始/继续，F6=暂停/继续（游戏中也直接生效，可在设置里改）。");
        if (OperatingSystem.IsWindows()) SetupTray();
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
        int startIdx = Math.Max(0, HotkeyStartCombo.SelectedIndex);
        int pauseIdx = Math.Max(0, HotkeyPauseCombo.SelectedIndex);
        int startCode = startIdx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(startIdx) : 0;
        int pauseCode = pauseIdx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(pauseIdx) : 0;
        Input.GlobalHotkeys.SetActive(new[] { startCode, pauseCode });
        if (startCode != 0 && startCode == pauseCode)
            InsertLog("警告：开始与暂停热键相同，将只执行“开始”动作。");
    }

    private void OnGlobalKeyWorker(int code, bool down)
    {
        UiPost(() => HandleGlobalKey(code));
    }

    private void HandleGlobalKey(int code)
    {
        int startIdx = Math.Max(0, HotkeyStartCombo.SelectedIndex);
        int pauseIdx = Math.Max(0, HotkeyPauseCombo.SelectedIndex);
        int startCode = startIdx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(startIdx) : 0;
        int pauseCode = pauseIdx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(pauseIdx) : 0;

        if (code == startCode && code != 0)
        {
            HotkeyStart();
        }
        else if (code == pauseCode && code != 0)
        {
            TogglePause();
        }
    }

    private void HotkeyStart()
    {
        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            if (eng.IsPaused)
            {
                eng.Resume();
                LblStatus.Foreground = Avalonia.Media.Brushes.SeaGreen;
                LblStatus.Text = "演奏中…";
                InsertLog("已继续播放（热键）。");
            }
            else
            {
                InsertLog("已在播放中。");
            }
            return;
        }
        if (_busy)
        {
            InsertLog("正在倒计时准备中，稍候…（可再按一次开始或等待开始）");
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
            LblStatus.Text = "演奏中…";
        }
        else
        {
            eng.Pause();
            LblStatus.Foreground = Avalonia.Media.Brushes.Crimson;
            LblStatus.Text = "已暂停 —— 再按暂停热键继续";
        }
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
        _cfg.StartHotkeyIndex = Math.Clamp(HotkeyStartCombo.SelectedIndex, 0, 12);
        _cfg.PauseHotkeyIndex = Math.Clamp(HotkeyPauseCombo.SelectedIndex, 0, 12);
        _cfg.ChordRoot = ChkChordRoot.IsChecked == true;
        _cfg.Breath = ChkBreath.IsChecked == true;
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

            try
            {
                var parsed = MidiLoader.Parse(path);
                _parsed = parsed;
                _tracks.Clear();
                foreach (var c in parsed.Candidates) _tracks.Add(new TrackRowVM(c));

                LblFile.Text = System.IO.Path.GetFileName(path);
                InsertLog($"已载入 {System.IO.Path.GetFileName(path)}：{parsed.Candidates.Count} 个候选，时长 ≈ {parsed.DurationSec:F1}s，请在左侧点选一行作为主旋律");

                _selected = null;
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

    // ================= 主旋律选择 =================

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

    private MappingResult BuildMapping()
    {
        if (_selected == null) return new MappingResult();
        // 基准八度始终自动：取音符最集中的八度，使其可演奏音域（基准±1八度再加顶部高高音do/#do）覆盖尽可能多的音
        IReadOnlyList<RawNote> src = _selected.Candidate.Notes;
        if (ChkChordRoot.IsChecked == true)
            src = NoteMapper.ChordRootOnly(src);   // 和弦只弹根音
        return NoteMapper.Map(src, CurrentTranspose, manualBaseOctave: null);
    }

    private void Chord_Changed(object? sender, RoutedEventArgs e)
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

    private void RefreshPreview()
    {
        var okColor = Avalonia.Media.Brushes.SeaGreen;
        var warnColor = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B25A00"));

        if (_selected == null || _parsed == null)
        {
            LblMelody.Text = _parsed == null
                ? "打开 MIDI 文件，并在左侧单击一行作为主旋律。"
                : "已载入文件 —— 在左侧单击一行，将其作为要演奏的主旋律。";
            LblWarn.Text = "";
            LblWarn.Foreground = warnColor;
            UpdatePlayButton();
            return;
        }

        var m = BuildMapping();
        var cand = _selected.Candidate;

        LblMelody.Text = $"主旋律：轨道 {cand.TrackIndex + 1} / 声道 {cand.Channel + 1}「{cand.Name}」" +
                         $"（共 {cand.NoteCount} 音）";

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
        UpdatePlayButton();
    }

    private void UpdatePlayButton()
    {
        BtnPlay.IsEnabled = !_busy && _selected != null && BuildMapping().InRangeCount > 0;
    }

    // ================= 播放 =================

    private void BtnPlay_Click(object? sender, RoutedEventArgs e) => RequestPlay();

    private void RequestPlay()
    {
        if (_busy || _selected == null) return;

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
        LblStatus.Text = $"{_countdownLeft} 秒后开始 —— 请切到游戏并装备口琴";
    }

    private void StartPlayback()
    {
        if (_playNotes.Count == 0) return;

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
        engine.Play(_playNotes, speed, FixedLeadMs, loop, breath);
        SliderProgress.Maximum = Math.Max(0.1, engine.TotalSeconds);
        LblStatus.Foreground = Avalonia.Media.Brushes.SeaGreen;
        LblStatus.Text = "演奏中…";

        // 开始吹奏后自动最小化，方便直接操作游戏（托盘可随时控制）
        if (WindowState != WindowState.Minimized)
            WindowState = WindowState.Minimized;

        BtnStop.IsEnabled = true;
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
        LblStatus.Text = "";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnOpen.IsEnabled = !busy;
        TrackList.IsEnabled = !busy;
        ChkLoop.IsEnabled = !busy;
        ChkChordRoot.IsEnabled = !busy;
        ChkBreath.IsEnabled = !busy;
        CountdownCombo.IsEnabled = !busy;
        BtnStop.IsEnabled = busy;
        UpdatePlayButton();
        // 速度 / 移调两个滑条：空闲与播放中都可调（播放中实时生效）
        SliderSpeed.IsEnabled = true;
        SliderTranspose.IsEnabled = true;
    }

    // ================= 托盘 =================

    private void SetupTray()
    {
        try
        {
            if (_tray != null || !OperatingSystem.IsWindows()) return;
            WindowIcon? icon = null;
            string p = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(p)) icon = new WindowIcon(File.OpenRead(p));

            var menu = new NativeMenu();
            var miShow = new NativeMenuItem { Header = "显示主窗口" };
            miShow.Click += (_, _) => ShowMainWindow();
            var miStart = new NativeMenuItem { Header = "开始 / 继续" };
            miStart.Click += (_, _) => HotkeyStart();
            var miPause = new NativeMenuItem { Header = "暂停 / 继续" };
            miPause.Click += (_, _) => TogglePause();
            var miStop = new NativeMenuItem { Header = "停止" };
            miStop.Click += (_, _) => StopPlaybackNow();
            var miQuit = new NativeMenuItem { Header = "退出" };
            miQuit.Click += (_, _) => QuitApp();
            menu.Items.Add(miShow);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miStart);
            menu.Items.Add(miPause);
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
        }
        catch (Exception ex)
        {
            InsertLog($"托盘初始化失败：{ex.Message}");
        }
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
        if (!_quitNow && OperatingSystem.IsWindows())
        {
            // Windows：点窗口 × → 隐藏到托盘后台，可随时从托盘恢复/退出
            e.Cancel = true;
            Hide();
            return;
        }

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
