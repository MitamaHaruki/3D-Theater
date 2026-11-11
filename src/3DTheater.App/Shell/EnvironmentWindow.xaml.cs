using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Theater3D.App.Rendering;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 環境の画像の設定（レンダリングのメニュー）。ON / OFF と、ファイル・明るさ・回転・背景に表示するかを変える。
/// 変えるたびに作品の描き方（<see cref="RenderSettings.Environment"/>）へ入れるので、すぐにプレビューへ反映し、プロジェクトは変更ありになる。
/// ファイルを変えると、その場で読み込む（<see cref="EnvironmentImage"/>）。読めたかどうかは画面に出す。
/// </summary>
public partial class EnvironmentWindow : Window
{
    private readonly RenderSettings? _settings;
    private readonly EnvironmentImage? _image;
    private bool _loading;

    internal EnvironmentWindow(RenderSettings settings, EnvironmentImage image)
    {
        InitializeComponent();
        _settings = settings;
        _image = image;
        ShowValues(settings.Environment);
        image.Changed += OnImageChanged;
        Closed += (_, _) => image.Changed -= OnImageChanged;
    }

    private void ShowValues(SceneEnvironment environment)
    {
        _loading = true;
        try
        {
            EnabledCheck.IsChecked = environment.Enabled;
            SetFileText(environment.File ?? string.Empty);
            IntensitySlider.Value = environment.Intensity;
            RotationSlider.Value = environment.Rotation;
            BackgroundCheck.IsChecked = environment.ShowBackground;
        }
        finally
        {
            _loading = false;
        }
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        IntensityText.Text = IntensitySlider.Value.ToString("0.00", c);
        RotationText.Text = RotationSlider.Value.ToString("0", c) + " 度";
        StatusText.Text = Status();
    }

    private string Status()
    {
        if (_image is null || _settings is null)
        {
            return string.Empty;
        }
        SceneEnvironment environment = _settings.Environment;
        if (string.IsNullOrWhiteSpace(environment.File))
        {
            return "「参照...」で HDR の画像（.hdr）を選んでください。";
        }
        if (!environment.Enabled)
        {
            return "使う（上のチェック）にすると読み込みます。";
        }
        if (_image.Error is { } error)
        {
            return "読み込めませんでした: " + error;
        }
        if (_image.Current is { } current)
        {
            var (width, height) = _image.SourceSize;
            return width == current.Width
                ? $"読み込みました（{width} x {height}）。"
                : $"読み込みました（{width} x {height}。{current.Width} x {current.Height} に縮めて使います）。";
        }
        return string.Empty;
    }

    private void Apply()
    {
        if (_loading || _settings is null)
        {
            return; // 作っている途中（InitializeComponent の中）と、値を出している間は入れない
        }
        Mouse.OverrideCursor = Cursors.Wait; // 大きな画像は読み込みに少しかかる
        try
        {
            _settings.Environment = new SceneEnvironment
            {
                Enabled = EnabledCheck.IsChecked == true,
                File = string.IsNullOrWhiteSpace(FileBox.Text) ? null : FileBox.Text,
                Intensity = (float)Math.Round(IntensitySlider.Value, 2),
                Rotation = (float)Math.Round(RotationSlider.Value, 0),
                ShowBackground = BackgroundCheck.IsChecked == true,
            };
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        UpdateTexts();
    }

    private void OnImageChanged(object? sender, EventArgs e) => UpdateTexts();

    /// <summary>ファイルの欄に出す。長いパスでもファイル名が見えるよう、終わりの側を見せる（全体はマウスを乗せると出る）。</summary>
    private void SetFileText(string text)
    {
        FileBox.Text = text;
        FileBox.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        Dispatcher.BeginInvoke(() => FileBox.ScrollToHorizontalOffset(double.PositiveInfinity), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "環境の画像を選ぶ",
            Filter = "HDR 画像 (*.hdr)|*.hdr|すべてのファイル (*.*)|*.*",
        };
        if (File.Exists(FileBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(FileBox.Text);
        }
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        _loading = true;
        SetFileText(dialog.FileName);
        EnabledCheck.IsChecked = true; // 選んだら使う
        _loading = false;
        Apply();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ShowValues(SceneEnvironment.Off);
        if (_settings is not null)
        {
            _settings.Environment = SceneEnvironment.Off;
        }
        UpdateTexts();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
