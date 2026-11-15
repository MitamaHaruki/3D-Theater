using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 霧の設定（レンダリングのメニュー）。ON / OFF と、色・始まる距離・見通せる距離・地面の近くほど濃くするかを変える。
/// 変えるたびに作品の描き方（<see cref="RenderSettings.Fog"/>）へ入れるので、すぐにプレビューへ反映し、プロジェクトは変更ありになる。
/// </summary>
public partial class FogWindow : Window, IShowsCurrentValues
{
    private readonly RenderSettings? _settings;
    private bool _loading;

    internal FogWindow(RenderSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ShowValues(settings.Fog);
    }

    /// <summary>設定の今の値を出し直す（ライブモードの設定画面で、ほかの所で変わったとき）。</summary>
    public void ShowCurrentValues()
    {
        if (_settings is not null)
        {
            ShowValues(_settings.Fog);
        }
    }

    private void ShowValues(SceneFog fog)
    {
        _loading = true;
        try
        {
            EnabledCheck.IsChecked = fog.Enabled;
            ColorBox.Text = fog.Color;
            StartSlider.Value = fog.Start;
            VisibilitySlider.Value = fog.Visibility;
            GroundOnlyCheck.IsChecked = fog.GroundOnly;
            HeightSlider.Value = fog.Height;
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
        StartText.Text = StartSlider.Value.ToString("0", c);
        VisibilityText.Text = VisibilitySlider.Value.ToString("0", c);
        HeightText.Text = HeightSlider.Value.ToString("0.0", c);
        ColorSwatch.Background = ImageExport.TryParseColor(ColorBox.Text, out Vector3 color)
            ? new SolidColorBrush(Color.FromRgb((byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255)))
            : Brushes.Transparent;
    }

    private void Apply()
    {
        if (_loading || _settings is null)
        {
            return; // 作っている途中（InitializeComponent の中）と、値を出している間は入れない
        }
        UpdateTexts();
        SceneFog current = _settings.Fog;
        _settings.Fog = new SceneFog
        {
            Enabled = EnabledCheck.IsChecked == true,
            Color = ImageExport.TryParseColor(ColorBox.Text, out _) ? ColorBox.Text.Trim() : current.Color, // 打ちかけの色は入れない
            Start = (float)Math.Round(StartSlider.Value, 0),
            Visibility = (float)Math.Round(VisibilitySlider.Value, 0),
            GroundOnly = GroundOnlyCheck.IsChecked == true,
            Height = (float)Math.Round(HeightSlider.Value, 1),
        };
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnColorChanged(object sender, TextChangedEventArgs e) => Apply();

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ShowValues(SceneFog.Off);
        if (_settings is not null)
        {
            _settings.Fog = SceneFog.Off;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
