using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GameInstrumentPlayer.Input;
using GameInstrumentPlayer.Midi;
using GameInstrumentPlayer.Profiles;

namespace GameInstrumentPlayer;

/// <summary>
/// 乐器方案编辑器：改琴键、改升半音方式、改输出与时序。
/// 内置方案先复制再改，保存时写成用户方案文件，随时可以删掉重来。
/// </summary>
public partial class ProfileEditorWindow : Window
{
    private readonly InstrumentProfile _p;
    private readonly bool _isNew;
    private bool _loading = true;

    /// <summary>保存后的方案（取消时为 null）。</summary>
    public InstrumentProfile? Result { get; private set; }

    /// <summary>键盘可选的键名（与 InputSender 支持的范围一致）。</summary>
    private static readonly string[] KeyNames =
    {
        "Z", "X", "C", "V", "B", "N", "M",
        "A", "S", "D", "F", "G", "H", "J", "K", "L",
        "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        ",", ".", ";", "/", "-", "=", "[", "]", "\\", "'", "`"
    };

    /// <summary>修饰键可选项。</summary>
    private static readonly string[] ModifierNames =
    {
        "", "LeftShift", "RightShift", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "Space", "Tab"
    };

    /// <summary>琴键格子：记录它在哪一行、第几个键。</summary>
    private sealed class KeyCell
    {
        public required Button Button { get; init; }
        public required ProfileRow Row { get; init; }
        public required int Index { get; init; }
    }

    private readonly List<KeyCell> _cells = new();

    private DispatcherTimer? _testTimer;
    private int _testStep;

    // 设计器需要无参构造
    public ProfileEditorWindow() : this(BuiltInProfiles.Default(), false) { }

    public ProfileEditorWindow(InstrumentProfile profile, bool isNew)
    {
        InitializeComponent();
        _p = profile;
        _isNew = isNew || !profile.UserDefined;

        // 编辑内置方案时，这里其实是在做一份副本
        Title = _isNew ? "新建乐器方案（基于当前方案）" : "编辑乐器方案";
        BtnDelete.IsVisible = profile.UserDefined && !_isNew;

        ComboSharp.ItemsSource = SharpModes.Labels;
        ComboSharpMod.ItemsSource = ModifierNames;
        ComboOutput.ItemsSource = OutputMethods.All.Select(OutputMethods.Label).ToList();

        TxtDir.Text = ProfileStore.DirPath;

        LoadFromProfile();
        _loading = false;
        RefreshStatus();
    }

    // ================= 载入 / 回写 =================

    private void LoadFromProfile()
    {
        TxtName.Text = _p.Name;

        NumPerRow.Value = Math.Clamp(_p.NotesPerRow, 1, 12);
        NumBasePitch.Value = Math.Clamp(_p.BasePitch, 0, 127);
        TxtOffsets.Text = string.Join(" ", _p.NoteOffsets);
        NumAutoRange.Value = Math.Clamp(_p.AutoOctaveRange, 0, 3);

        ComboSharp.SelectedIndex = SharpModes.IndexOf(_p.SharpMode);
        ComboSharpMod.SelectedIndex = Math.Max(0, Array.IndexOf(ModifierNames, _p.SharpModifier));
        NumSharpRow.Value = Math.Clamp(_p.SharpRowOffset, -3, 3);

        int outIdx = Array.IndexOf(OutputMethods.All, _p.OutputMethod);
        ComboOutput.SelectedIndex = outIdx < 0 ? 0 : outIdx;

        NumHold.Value = Math.Clamp(_p.HoldMs, 5, 500);
        NumRetrigger.Value = Math.Clamp(_p.RetriggerMs, 5, 500);
        NumModLead.Value = Math.Clamp(_p.ModLeadMs, 0, 500);
        NumGap.Value = Math.Clamp(_p.ReleaseGapMs, 0, 500);
        NumFrame.Value = Math.Clamp(_p.FrameMs, 1, 100);

        RebuildRows();
        UpdateSharpHint();
        UpdateBaseName();
    }

    /// <summary>把界面上的值写回 _p；返回校验错误（空 = 通过）。</summary>
    private string Collect()
    {
        _p.Name = string.IsNullOrWhiteSpace(TxtName.Text) ? "未命名方案" : TxtName.Text!.Trim();
        _p.NotesPerRow = (int)(NumPerRow.Value ?? 7);
        _p.BasePitch = (int)(NumBasePitch.Value ?? 60);
        _p.AutoOctaveRange = (int)(NumAutoRange.Value ?? 1);
        _p.SharpMode = SharpModes.FromIndex(ComboSharp.SelectedIndex);
        _p.SharpModifier = ComboSharpMod.SelectedIndex <= 0 ? "" : ModifierNames[ComboSharpMod.SelectedIndex];
        _p.SharpRowOffset = (int)(NumSharpRow.Value ?? 0);
        _p.OutputMethod = OutputMethods.All[Math.Clamp(ComboOutput.SelectedIndex, 0, OutputMethods.All.Length - 1)];
        _p.HoldMs = (int)(NumHold.Value ?? 40);
        _p.RetriggerMs = (int)(NumRetrigger.Value ?? 45);
        _p.ModLeadMs = (int)(NumModLead.Value ?? 40);
        _p.ReleaseGapMs = (int)(NumGap.Value ?? 40);
        _p.FrameMs = (int)(NumFrame.Value ?? 17);

        var offsets = ParseOffsets(TxtOffsets.Text);
        if (offsets.Count > 0) _p.NoteOffsets = offsets;

        return _p.Validate();
    }

    private static List<int> ParseOffsets(string? text)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string part in text.Split(new[] { ' ', ',', '，', '、', ';', '；', '/', '|' },
                                           StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int v) && v is >= 0 and <= 11 && !list.Contains(v)) list.Add(v);
        }
        list.Sort();
        return list;
    }

    private void RefreshStatus()
    {
        string check = _p.Validate();
        TxtStatus.Text = check.Length == 0 ? "" : "⚠ " + check;
        BtnSave.IsEnabled = check.Length == 0;
        TxtHint.Text = $"{_p.RangeLabel}；共 {_p.RowCount} 行。" +
                       "改完点「保存方案」。内置方案改动会存成你自己的方案，原方案不受影响。";
    }

    private void UpdateBaseName()
    {
        int p = (int)(NumBasePitch.Value ?? 60);
        TxtBaseName.Text = $"= {Music.NoteName(p)}";
    }

    private void UpdateSharpHint()
    {
        var mode = SharpModes.FromIndex(ComboSharp.SelectedIndex);
        ComboSharpMod.IsVisible = mode == SharpMode.Modifier;
        NumSharpRow.IsVisible = mode == SharpMode.RowShift;
        TxtSharpHint.Text = mode switch
        {
            SharpMode.Modifier => "先把修饰键按下，再按琴键，然后松开。多数游戏的口琴、竖琴用这种。",
            SharpMode.RowShift => "用另一行的音代替半音，适合每行半音齐全、但没有修饰键的乐器。",
            SharpMode.Snap => "把 #do 当成 do 演奏。音会略微走音，但不会漏音。",
            _ => "遇到 # 音直接空拍。谱面会缺音，一般不推荐。"
        };
    }

    // ================= 琴键布局 =================

    private void RebuildRows()
    {
        _cells.Clear();
        RowsPanel.Children.Clear();

        int perRow = (int)(NumPerRow.Value ?? 7);
        for (int r = 0; r < _p.Rows.Count; r++)
        {
            var row = _p.Rows[r];
            RowsPanel.Children.Add(BuildRowEditor(row, perRow, r));
        }
    }

    private Control BuildRowEditor(ProfileRow row, int perRow, int rowIndex)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var root = _p.BasePitch + row.OctaveOffset * 12;
        panel.Children.Add(new TextBlock
        {
            Text = row.OctaveOffset == 0 ? $"基准行（{Music.NoteName(root)} 起）" : $"+{row.OctaveOffset} 八度（{Music.NoteName(root)} 起）",
            Width = 150,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (IBrush?)this.FindResource("BrushTextSecondary") ?? Brushes.Gray
        });

        for (int i = 0; i < perRow; i++)
        {
            while (row.Keys.Count <= i) row.Keys.Add("");
            var button = new Button
            {
                Width = 52,
                Padding = new Avalonia.Thickness(0, 4),
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            var cell = new KeyCell { Button = button, Row = row, Index = i };
            _cells.Add(cell);
            PaintCell(cell);
            button.Click += (_, _) => ShowKeyPicker(cell);
            panel.Children.Add(button);
        }

        var remove = new Button
        {
            Content = "✕",
            Width = 32,
            Padding = new Avalonia.Thickness(0, 4),
            FontSize = 12,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            IsVisible = _p.Rows.Count > 1
        };
        remove.Click += (_, _) =>
        {
            _p.Rows.Remove(row);
            RebuildRows();
            RefreshStatus();
        };
        panel.Children.Add(remove);

        // 换行标签放在最后，方便多个八度时对齐
        if (rowIndex == 0 && _p.Rows.Count > 1)
            panel.Children.Add(new TextBlock
            {
                Text = "←「✕」删掉这一行",
                FontSize = 11,
                Margin = new Avalonia.Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (IBrush?)this.FindResource("BrushTextMuted") ?? Brushes.Gray
            });

        return panel;
    }

    private void PaintCell(KeyCell cell)
    {
        var action = ActionCodec.Parse(cell.Row.Keys[cell.Index]);
        cell.Button.Content = action.IsNone ? "无" : action.Label;
        cell.Button.Opacity = action.IsNone ? 0.45 : 1.0;
    }

    private void ShowKeyPicker(KeyCell cell)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>
        {
            MakeItem("无（这个位置没有琴键）", null, () =>
            {
                cell.Row.Keys[cell.Index] = "";
                PaintCell(cell);
                RefreshStatus();
            })
        };

        foreach (string key in KeyNames)
        {
            var action = new NoteAction(ActionKind.Key, key[0]);
            items.Add(MakeItem($"键盘 {key}", action.Token, () =>
            {
                cell.Row.Keys[cell.Index] = action.Token;
                PaintCell(cell);
                RefreshStatus();
            }));
        }
        foreach (var (label, id) in new[]
        {
            ("鼠标左键", MouseButtonId.Left),
            ("鼠标中键", MouseButtonId.Middle),
            ("鼠标右键", MouseButtonId.Right),
            ("鼠标侧键 1", MouseButtonId.X1),
            ("鼠标侧键 2", MouseButtonId.X2)
        })
        {
            var action = new NoteAction(ActionKind.Mouse, (int)id + 1);
            items.Add(MakeItem(label, action.Token, () =>
            {
                cell.Row.Keys[cell.Index] = action.Token;
                PaintCell(cell);
                RefreshStatus();
            }));
        }

        menu.ItemsSource = items;
        menu.Open(cell.Button);
    }

    private static MenuItem MakeItem(string header, string? tag, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        if (tag != null) mi.Tag = tag;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    // ================= 控件事件 =================

    private void PerRow_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        int perRow = (int)(NumPerRow.Value ?? 7);
        foreach (var row in _p.Rows)
        {
            while (row.Keys.Count < perRow) row.Keys.Add("");
            while (row.Keys.Count > perRow) row.Keys.RemoveAt(row.Keys.Count - 1);
        }
        RebuildRows();
        RefreshStatus();
    }

    private void BasePitch_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        _p.BasePitch = (int)(NumBasePitch.Value ?? 60);
        UpdateBaseName();
        RebuildRows();
        RefreshStatus();
    }

    private void Offsets_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var offsets = ParseOffsets(TxtOffsets.Text);
        if (offsets.Count == 0) return;
        _p.NoteOffsets = offsets;
        RefreshStatus();
    }

    private void Sharp_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateSharpHint();
        RefreshStatus();
    }

    private void SharpMod_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshStatus();
    }

    private void SharpRow_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        RefreshStatus();
    }

    private void Output_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        TxtOutputHint.Text = ComboOutput.SelectedIndex == 1
            ? "虚拟键码：少数游戏只认这种方式。多数游戏用「键盘扫描码」即可。"
            : "键盘扫描码：推荐。绝大多数游戏与 DirectInput 都是这种。";
    }

    private void AutoRange_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        RefreshStatus();
    }

    private void Timing_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        RefreshStatus();
    }

    private void BtnAddRow_Click(object? sender, RoutedEventArgs e)
    {
        if (_p.Rows.Count >= 6) return;
        int top = _p.Rows.Count == 0 ? 0 : _p.Rows.Max(r => r.OctaveOffset);
        int perRow = (int)(NumPerRow.Value ?? 7);
        var row = new ProfileRow { OctaveOffset = top + 1 };
        for (int i = 0; i < perRow; i++) row.Keys.Add("");
        _p.Rows.Add(row);
        RebuildRows();
        RefreshStatus();
    }

    // ================= 试按键 =================

    /// <summary>把当前方案每一行的琴键按顺序发一遍，让用户在游戏里确认按键能收到。</summary>
    private async void BtnTest_Click(object? sender, RoutedEventArgs e)
    {
        if (_testTimer != null)
        {
            _testTimer.Stop();
            _testTimer = null;
            BtnTest.Content = "试按键";
            return;
        }
        if (!InputSender.IsSupported)
        {
            TxtStatus.Text = "当前系统不支持模拟按键（需要 Windows）。";
            return;
        }

        string check = Collect();
        if (check.Length != 0) { RefreshStatus(); return; }

        var sequence = new List<NoteAction>();
        foreach (var row in _p.Rows)
            foreach (string k in row.Keys)
            {
                var a = ActionCodec.Parse(k);
                if (!a.IsNone) sequence.Add(a);
            }
        if (sequence.Count == 0) return;

        BtnTest.Content = "停止试按";
        _testStep = 0;
        bool vk = _p.OutputMethod == OutputMethods.KeyboardVirtualKey;
        int hold = Math.Clamp(_p.HoldMs, 20, 200);

        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(hold + 60) };
        _testTimer.Tick += (_, _) =>
        {
            if (_testStep >= sequence.Count)
            {
                _testTimer!.Stop();
                _testTimer = null;
                BtnTest.Content = "试按键";
                InputSender.ReleaseEverything();
                TxtStatus.Text = "试按键结束。若游戏没有反应，先切到游戏窗口再点「试按键」。";
                return;
            }
            InputSender.Send(sequence[_testStep], true, vk);
            var a = sequence[_testStep];
            DispatcherTimer.RunOnce(() => InputSender.Send(a, false, vk), TimeSpan.FromMilliseconds(hold));
            _testStep++;
        };
        _testTimer.Start();
        RefreshStatus();
        TxtStatus.Text = "正在按顺序发琴键。请先切到游戏窗口让它获得焦点（本窗口会挡住按键）。";
        await Task.Yield();
    }

    // ================= 保存 / 导入 / 导出 / 删除 =================

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        string check = Collect();
        if (check.Length != 0) { RefreshStatus(); return; }

        if (string.IsNullOrWhiteSpace(_p.Id) || _isNew) _p.Id = _p.LayoutFingerprint();
        _p.UserDefined = true;

        if (!ProfileStore.Save(_p, out string err))
        {
            TxtStatus.Text = "保存失败：" + err;
            return;
        }
        Result = _p;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _testTimer?.Stop();
        InputSender.ReleaseEverything();
        Close(false);
    }

    private async void BtnImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入乐器方案",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("方案文件") { Patterns = new List<string> { "*.json" } },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            if (!ProfileStore.TryFromJson(await File.ReadAllTextAsync(path), out var loaded, out string err) || loaded == null)
            {
                TxtStatus.Text = "导入失败：" + err;
                return;
            }
            loaded.UserDefined = true;
            _p.Id = loaded.Id;
            _p.Name = loaded.Name;
            _p.Description = loaded.Description;
            _p.NotesPerRow = loaded.NotesPerRow;
            _p.NoteOffsets = loaded.NoteOffsets;
            _p.BasePitch = loaded.BasePitch;
            _p.AutoOctaveRange = loaded.AutoOctaveRange;
            _p.SharpMode = loaded.SharpMode;
            _p.SharpModifier = loaded.SharpModifier;
            _p.SharpRowOffset = loaded.SharpRowOffset;
            _p.OutputMethod = loaded.OutputMethod;
            _p.HoldMs = loaded.HoldMs;
            _p.RetriggerMs = loaded.RetriggerMs;
            _p.ModLeadMs = loaded.ModLeadMs;
            _p.ReleaseGapMs = loaded.ReleaseGapMs;
            _p.FrameMs = loaded.FrameMs;
            _p.Rows = loaded.Rows;

            _loading = true;
            LoadFromProfile();
            _loading = false;
            RefreshStatus();
            TxtStatus.Text = $"已导入：{_p.Name}。点「保存方案」才会写入本机。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入失败：" + ex.Message;
        }
    }

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        string check = Collect();
        if (check.Length != 0) { RefreshStatus(); return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出乐器方案",
                SuggestedFileName = _p.Name + ".json",
                DefaultExtension = "json",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("方案文件") { Patterns = new List<string> { "*.json" } }
                }
            });
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            await File.WriteAllTextAsync(path, ProfileStore.ToJson(_p), new System.Text.UTF8Encoding(true));
            TxtStatus.Text = "已导出：" + path;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导出失败：" + ex.Message;
        }
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (!_p.UserDefined)
        {
            TxtStatus.Text = "内置方案不能删除。可以另存一份再改。";
            return;
        }
        if (!ProfileStore.Delete(_p, out string err))
        {
            TxtStatus.Text = "删除失败：" + err;
            return;
        }
        Result = null;
        Close(true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _testTimer?.Stop();
            InputSender.ReleaseEverything();
            Close(false);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
