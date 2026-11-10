using System.Globalization;
using System.Windows;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 床の映り込みの設定（レンダリングのメニュー）。ON / OFF と、濃さ・消えるまでの距離を変える。
/// 変えるたびに作品の描き方（<see cref="RenderSettings.Reflection"/>）へ入れるので、すぐにプレビューへ反映し、プロジェクトは変更ありになる。
/// </summary>
public partial class ReflectionWindow : Window
{
    private readonly RenderSettings? _settings;
    private bool _loading;

    internal ReflectionWindow(RenderSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ShowValues(settings.Reflection);
    }

    private void ShowValues(FloorReflection value)
    {
        _loading = true;
        try
        {
            EnabledCheck.IsChecked = value.Enabled;
            StrengthSlider.Value = value.Strength;
            FadeSlider.Value = value.Fade;
        }
        finally
        {
            _loading = false;
        }
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        StrengthText.Text = StrengthSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        FadeText.Text = FadeSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void Apply()
    {
        if (_loading || _settings is null)
        {
            return; // 作っている途中（InitializeComponent の中）と、値を出している間は入れない
        }
        UpdateTexts();
        _settings.Reflection = new FloorReflection
        {
            Enabled = EnabledCheck.IsChecked == true,
            Strength = (float)Math.Round(StrengthSlider.Value, 2),
            Fade = (float)Math.Round(FadeSlider.Value, 1),
        };
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ShowValues(FloorReflection.Off);
        if (_settings is not null)
        {
            _settings.Reflection = FloorReflection.Off;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
