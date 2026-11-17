using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの「キー操作」パネル。キーごとに、キー（押して決める）・動かすもの・モデル・名前（表情のプリセット・モーフ）・押し方を決め、
/// 「試す」で動かして確かめられる。キーの様子（効く・ほかのアプリが使っている など）と、入っているかを出す。
/// 行はキーごとに作って持ち続け、決まりが変わったら中身だけを入れ替える（選んでいる途中の欄が作り直されないように）。
/// </summary>
public partial class HotkeyPanel : UserControl
{
    private const string AutoModel = "（トラッキングのキャラクター）";
    private static readonly Brush ProblemBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40));

    private readonly EditorContext _context;
    private readonly HotkeySettings _settings;
    private readonly HotkeyService _service;
    private readonly Dictionary<Guid, Row> _rows = [];
    private bool _updating;
    private Row? _capturing;

    internal HotkeyPanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _settings = context.LiveHotkeys;
        _service = context.Hotkeys!;
        _settings.Changed += (_, _) => Sync();
        _service.StateChanged += (_, _) => UpdateStatuses();
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.CamerasGroup && e.PropertyName == nameof(SceneNode.Children))
                || (e.Node is CameraNode && e.PropertyName == nameof(SceneNode.Name)))
            {
                Sync(); // カメラの切り替えの候補
            }
        };
        context.MorphPresets.Changed += (_, _) => Sync();
        Loaded += (_, _) => Sync();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        HotkeyBinding binding = HotkeyBinding.Create();
        if (!_settings.Add(binding))
        {
            return; // 多すぎる
        }
        if (_rows.TryGetValue(binding.Id, out Row? row))
        {
            BeginCapture(row); // 足したら、そのままキーを決める
        }
    }

    // ===== 行 =====
    private sealed class Row(Guid id)
    {
        public Guid Id { get; } = id;

        public Border Root { get; } = new() { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6), BorderThickness = new Thickness(1) };

        public Button KeyButton { get; } = new() { MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 2, 6, 2) };

        public ComboBox ActionCombo { get; } = new() { Margin = new Thickness(6, 0, 0, 0), MinHeight = 0, Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };

        public ComboBox ModelCombo { get; } = new() { MinHeight = 0, Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };

        public ComboBox NameCombo { get; } = new() { Margin = new Thickness(6, 0, 0, 0), MinHeight = 0, Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };

        public ComboBox ModeCombo { get; } = new() { MinHeight = 0, Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };

        public ComboBox DurationCombo { get; } = new() { MinHeight = 0, Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };

        public Button TryButton { get; } = new() { Content = "試す", MinWidth = 64, Margin = new Thickness(6, 0, 0, 0) };

        public Button DeleteButton { get; } = new() { Content = "", Margin = new Thickness(6, 0, 0, 0), ToolTip = "このキーを消す" };

        public TextBlock StatusText { get; } = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    }

    private Row CreateRow(Guid id)
    {
        var row = new Row(id);
        row.Root.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        row.DeleteButton.SetResourceReference(StyleProperty, "IconButtonStyle");
        foreach (HotkeyAction action in HotkeyActions.All)
        {
            row.ActionCombo.Items.Add(new ComboBoxItem { Content = HotkeyActions.DisplayName(action) });
        }
        foreach (HotkeyMode mode in Enum.GetValues<HotkeyMode>())
        {
            row.ModeCombo.Items.Add(new ComboBoxItem { Content = HotkeyActions.DisplayName(mode) });
        }
        foreach (var (_, label) in TimeOfDayHotkeys.Durations)
        {
            row.DurationCombo.Items.Add(new ComboBoxItem { Content = label });
        }

        var line1 = new DockPanel();
        DockPanel.SetDock(row.DeleteButton, Dock.Right);
        DockPanel.SetDock(row.KeyButton, Dock.Left);
        line1.Children.Add(row.DeleteButton);
        line1.Children.Add(row.KeyButton);
        line1.Children.Add(row.ActionCombo);

        var line2 = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        line2.ColumnDefinitions.Add(new ColumnDefinition());
        line2.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(row.NameCombo, 1);
        line2.Children.Add(row.ModelCombo);
        line2.Children.Add(row.NameCombo);

        var line3 = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(row.TryButton, Dock.Right);
        line3.Children.Add(row.TryButton);
        var modeOrDuration = new Grid(); // 押し方と、時刻の移り変わる長さは、どちらか一方だけを出す
        modeOrDuration.Children.Add(row.ModeCombo);
        modeOrDuration.Children.Add(row.DurationCombo);
        line3.Children.Add(modeOrDuration);

        var stack = new StackPanel();
        stack.Children.Add(line1);
        stack.Children.Add(line2);
        stack.Children.Add(line3);
        stack.Children.Add(row.StatusText);
        row.Root.Child = stack;

        row.KeyButton.Click += (_, _) => BeginCapture(row);
        row.KeyButton.PreviewKeyDown += (_, e) => OnCaptureKey(row, e);
        row.KeyButton.LostKeyboardFocus += (_, _) => EndCapture(row, null);
        row.ActionCombo.SelectionChanged += (_, _) => Change(row, b =>
        {
            HotkeyAction action = HotkeyActions.All[Math.Max(0, row.ActionCombo.SelectedIndex)];
            // モデルとカメラは同じ欄に持つので、モデルを使うものどうしの間だけ選んだモデルを残す（カメラはライブカメラから）
            bool keepModel = HotkeyActions.UsesModel(action) && HotkeyActions.UsesModel(b.Action);
            // 時刻は「次の時刻へ」から始める（選ばないままにしない）
            return action == b.Action ? b : b with
            {
                Action = action,
                Model = keepModel ? b.Model : null,
                Name = action == HotkeyAction.TimeOfDay ? TimeOfDayHotkeys.Next : null,
            };
        });
        row.ModelCombo.DropDownOpened += (_, _) => FillModels(row, Binding(row));
        row.ModelCombo.SelectionChanged += (_, _) => Change(row, b => b with { Model = (row.ModelCombo.SelectedItem as ComboBoxItem)?.Tag as Guid? });
        row.NameCombo.DropDownOpened += (_, _) => FillNames(row, Binding(row));
        row.NameCombo.SelectionChanged += (_, _) => Change(row, b => b with { Name = (row.NameCombo.SelectedItem as ComboBoxItem)?.Tag as string });
        row.ModeCombo.SelectionChanged += (_, _) => Change(row, b => b with { Mode = row.ModeCombo.SelectedIndex == 1 ? HotkeyMode.Hold : HotkeyMode.Toggle });
        row.DurationCombo.SelectionChanged += (_, _) => Change(row, b => row.DurationCombo.SelectedIndex < 0
            ? b
            : b with { Duration = TimeOfDayHotkeys.Durations[row.DurationCombo.SelectedIndex].Seconds });
        row.TryButton.Click += (_, _) => _service.Try(row.Id);
        row.DeleteButton.Click += (_, _) => _settings.Remove(row.Id);
        return row;
    }

    private HotkeyBinding? Binding(Row row) => _settings.Find(row.Id);

    // 欄を変えたら、決まりを置き換える（中身を入れている間は何もしない）
    private void Change(Row row, Func<HotkeyBinding, HotkeyBinding> change)
    {
        if (_updating || Binding(row) is not { } binding)
        {
            return;
        }
        _settings.Replace(change(binding));
    }

    private void Sync()
    {
        foreach (Guid id in _rows.Keys.Where(id => _settings.Find(id) is null).ToArray())
        {
            RowsHost.Children.Remove(_rows[id].Root);
            _rows.Remove(id);
        }
        int number = 0;
        foreach (HotkeyBinding binding in _settings.Bindings)
        {
            if (!_rows.TryGetValue(binding.Id, out Row? row))
            {
                row = CreateRow(binding.Id);
                _rows[binding.Id] = row;
            }
            if (RowsHost.Children.IndexOf(row.Root) != number)
            {
                RowsHost.Children.Remove(row.Root);
                RowsHost.Children.Insert(number, row.Root);
            }
            number++;
            Show(row, binding, number);
        }
        EmptyText.Visibility = _settings.Bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatuses();
    }

    private void Show(Row row, HotkeyBinding binding, int number)
    {
        _updating = true;
        try
        {
            if (_capturing != row)
            {
                row.KeyButton.Content = binding.Gesture?.ToString() ?? "（キーを決める）";
            }
            AutomationProperties.SetName(row.KeyButton, $"キー {number}");
            AutomationProperties.SetName(row.ActionCombo, $"動かすもの {number}");
            AutomationProperties.SetName(row.ModelCombo, HotkeyActions.UsesCamera(binding.Action) ? $"カメラ {number}" : $"モデル {number}");
            AutomationProperties.SetName(row.NameCombo, $"名前 {number}");
            AutomationProperties.SetName(row.ModeCombo, $"押し方 {number}");
            AutomationProperties.SetName(row.DurationCombo, $"移り変わり {number}");
            AutomationProperties.SetName(row.TryButton, $"試す {number}");
            AutomationProperties.SetName(row.DeleteButton, $"キーを消す {number}");
            AutomationProperties.SetAutomationId(row.StatusText, $"HotkeyStatus{number}");
            row.KeyButton.ToolTip = "押してから、決めたいキー（Ctrl・Alt・Shift と一緒に）を押します。Esc でやめます";

            row.ActionCombo.SelectedIndex = (int)binding.Action;
            bool usesModel = HotkeyActions.UsesModel(binding.Action) || HotkeyActions.UsesCamera(binding.Action);
            row.ModelCombo.Visibility = usesModel ? Visibility.Visible : Visibility.Collapsed;
            row.NameCombo.Visibility = HotkeyActions.UsesName(binding.Action) ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(row.ModelCombo, HotkeyActions.UsesName(binding.Action) ? 1 : 2);
            // モデルを選ばないもの（時刻）は、名前の欄を 1 行いっぱいに
            Grid.SetColumn(row.NameCombo, usesModel ? 1 : 0);
            Grid.SetColumnSpan(row.NameCombo, usesModel ? 1 : 2);
            row.NameCombo.Margin = new Thickness(usesModel ? 6 : 0, 0, 0, 0);
            row.ModeCombo.SelectedIndex = (int)binding.EffectiveMode;
            row.ModeCombo.IsEnabled = HotkeyActions.HasMode(binding.Action);
            bool usesDuration = HotkeyActions.UsesDuration(binding.Action);
            row.ModeCombo.Visibility = usesDuration ? Visibility.Collapsed : Visibility.Visible;
            row.DurationCombo.Visibility = usesDuration ? Visibility.Visible : Visibility.Collapsed;
            row.DurationCombo.SelectedIndex = NearestDuration(binding.Duration);
            FillModels(row, binding);
            FillNames(row, binding);
        }
        finally
        {
            _updating = false;
        }
    }

    // モデルの候補（表情は「トラッキングのキャラクター」も）。カメラの切り替えは、カメラの候補
    private void FillModels(Row row, HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return;
        }
        bool wasUpdating = _updating;
        _updating = true;
        try
        {
            row.ModelCombo.Items.Clear();
            if (HotkeyActions.UsesCamera(binding.Action))
            {
                row.ModelCombo.Items.Add(new ComboBoxItem { Content = _context.Scene.MainCamera.Name, Tag = null });
                foreach (CameraNode camera in _context.Scene.AddedCameras)
                {
                    row.ModelCombo.Items.Add(new ComboBoxItem { Content = camera.Name, Tag = camera.Id });
                }
                row.ModelCombo.SelectedIndex = row.ModelCombo.Items.Cast<ComboBoxItem>().ToList()
                    .FindIndex(i => (i.Tag as Guid?) == binding.Model);
                return;
            }
            if (binding.Action == HotkeyAction.Expression)
            {
                row.ModelCombo.Items.Add(new ComboBoxItem { Content = AutoModel, Tag = null });
            }
            foreach (ModelNode model in _context.Scene.ModelsGroup.Children.OfType<ModelNode>())
            {
                row.ModelCombo.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
            }
            row.ModelCombo.SelectedIndex = row.ModelCombo.Items.Cast<ComboBoxItem>().ToList()
                .FindIndex(i => (i.Tag as Guid?) == binding.Model);
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    // 名前の候補（表情はプリセット、モーフは選んだモデルのモーフ）
    private void FillNames(Row row, HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return;
        }
        bool wasUpdating = _updating;
        _updating = true;
        try
        {
            row.NameCombo.Items.Clear();
            if (binding.Action == HotkeyAction.TimeOfDay)
            {
                foreach (var (value, label) in TimeOfDayHotkeys.Choices)
                {
                    row.NameCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                }
                if (binding.Name is { } chosen && !TimeOfDayHotkeys.Choices.Any(c => c.Value == chosen))
                {
                    row.NameCombo.Items.Add(new ComboBoxItem { Content = TimeOfDayHotkeys.Label(chosen) ?? $"{chosen}（読めません）", Tag = chosen });
                }
                row.NameCombo.SelectedIndex = row.NameCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as string) == binding.Name);
                return;
            }
            IEnumerable<string> names = binding.Action switch
            {
                HotkeyAction.Expression => _context.MorphPresets.Presets.Select(p => p.Name),
                HotkeyAction.Morph => _context.Scene.ModelsGroup.Children.OfType<ModelNode>().FirstOrDefault(m => m.Id == binding.Model)?.Morphs
                    .Select(m => m.Name).Distinct() ?? [],
                _ => [],
            };
            foreach (string name in names)
            {
                row.NameCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
            if (binding.Name is { } current && !names.Contains(current))
            {
                row.NameCombo.Items.Add(new ComboBoxItem { Content = $"{current}（見つかりません）", Tag = current });
            }
            row.NameCombo.SelectedIndex = row.NameCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as string) == binding.Name);
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    // いちばん近い移り変わりの長さの番号
    private static int NearestDuration(double seconds)
    {
        int best = 0;
        for (int i = 1; i < TimeOfDayHotkeys.Durations.Count; i++)
        {
            if (Math.Abs(TimeOfDayHotkeys.Durations[i].Seconds - seconds) < Math.Abs(TimeOfDayHotkeys.Durations[best].Seconds - seconds))
            {
                best = i;
            }
        }
        return best;
    }

    private void UpdateStatuses()
    {
        foreach (Row row in _rows.Values)
        {
            string status = _service.StatusOf(row.Id);
            bool on = _service.IsOn(row.Id);
            row.StatusText.Text = (on ? "● 出しています　" : string.Empty) + status;
            if (status.StartsWith("効きます", StringComparison.Ordinal))
            {
                row.StatusText.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "SubTextBrush");
            }
            else
            {
                row.StatusText.Foreground = ProblemBrush;
            }
        }
    }

    // ===== キーを決める =====
    private void BeginCapture(Row row)
    {
        if (_capturing is not null && _capturing != row)
        {
            EndCapture(_capturing, null);
        }
        if (_capturing == row)
        {
            return;
        }
        _capturing = row;
        row.KeyButton.Content = "キーを押してください（Esc でやめる）";
        _service.Suspend(); // 決めたいキーが、登録したキーとして受け取られないように
        row.KeyButton.Focus();
        Keyboard.Focus(row.KeyButton);
    }

    private void OnCaptureKey(Row row, KeyEventArgs e)
    {
        if (_capturing != row)
        {
            return;
        }
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // 一緒に押すキーだけなら、続きを待つ
        }
        if (key == Key.Escape)
        {
            EndCapture(row, null);
            return;
        }
        var gesture = new HotkeyGesture(ToModifiers(Keyboard.Modifiers), KeyInterop.VirtualKeyFromKey(key));
        if (!gesture.IsValid)
        {
            row.StatusText.Text = "このキーは使えません（文字・数字・F1〜F24・テンキー・矢印などのキー）";
            row.StatusText.Foreground = ProblemBrush;
            return;
        }
        EndCapture(row, gesture);
    }

    private void EndCapture(Row row, HotkeyGesture? gesture)
    {
        if (_capturing != row)
        {
            return;
        }
        _capturing = null;
        _service.Resume();
        if (gesture is { } g && Binding(row) is { } binding)
        {
            _settings.Replace(binding with { Gesture = g });
        }
        if (Binding(row) is { } current)
        {
            row.KeyButton.Content = current.Gesture?.ToString() ?? "（キーを決める）";
        }
    }

    private static HotkeyModifiers ToModifiers(ModifierKeys keys)
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }
        if (keys.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }
        if (keys.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }
        if (keys.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }
        return modifiers;
    }
}
