using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 映像効果の設定（レンダリングのメニュー）。色調補正・ブルーム・周辺減光・環境光の遮り・画面の中の反射・被写界深度を ON / OFF し、値をつまみで変える。
/// 変えるたびに作品の描き方（<see cref="RenderSettings.Effects"/>）へ入れるので、すぐにプレビューへ反映し、プロジェクトは変更ありになる。
/// </summary>
public partial class EffectsWindow : Window
{
    private readonly RenderSettings? _settings;
    private bool _loading;

    internal EffectsWindow(RenderSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ShowValues(settings.Effects);
    }

    /// <summary>値を画面へ出す（出している間の変更は、設定へ入れ直さない）。</summary>
    private void ShowValues(VisualEffects effects)
    {
        _loading = true;
        try
        {
            ColorCheck.IsChecked = effects.ColorEnabled;
            ExposureSlider.Value = effects.Exposure;
            ContrastSlider.Value = effects.Contrast;
            SaturationSlider.Value = effects.Saturation;
            TemperatureSlider.Value = effects.Temperature;
            BloomCheck.IsChecked = effects.BloomEnabled;
            BloomIntensitySlider.Value = effects.BloomIntensity;
            BloomThresholdSlider.Value = effects.BloomThreshold;
            BloomSpreadSlider.Value = effects.BloomSpread;
            VignetteCheck.IsChecked = effects.VignetteEnabled;
            VignetteSlider.Value = effects.Vignette;
            AoCheck.IsChecked = effects.AoEnabled;
            AoStrengthSlider.Value = effects.AoStrength;
            AoRadiusSlider.Value = effects.AoRadius;
            SsrCheck.IsChecked = effects.SsrEnabled;
            SsrStrengthSlider.Value = effects.SsrStrength;
            SsrDistanceSlider.Value = effects.SsrDistance;
            SsrRayTracingCheck.IsChecked = effects.SsrRayTracing;
            DofCheck.IsChecked = effects.DofEnabled;
            FocusOnTargetCheck.IsChecked = effects.DofFocusOnTarget;
            FocusDistanceSlider.Value = effects.DofFocusDistance;
            FocusRangeSlider.Value = effects.DofFocusRange;
            DofBlurSlider.Value = effects.DofBlur;
        }
        finally
        {
            _loading = false;
        }
        UpdateTexts();
    }

    /// <summary>画面の値（小数 2 桁に丸める。保存したファイルが読みやすいように）。</summary>
    private VisualEffects Read()
    {
        static float Round(Slider slider) => (float)Math.Round(slider.Value, 2);
        return new VisualEffects
        {
            ColorEnabled = ColorCheck.IsChecked == true,
            Exposure = Round(ExposureSlider),
            Contrast = Round(ContrastSlider),
            Saturation = Round(SaturationSlider),
            Temperature = Round(TemperatureSlider),
            BloomEnabled = BloomCheck.IsChecked == true,
            BloomIntensity = Round(BloomIntensitySlider),
            BloomThreshold = Round(BloomThresholdSlider),
            BloomSpread = Round(BloomSpreadSlider),
            VignetteEnabled = VignetteCheck.IsChecked == true,
            Vignette = Round(VignetteSlider),
            AoEnabled = AoCheck.IsChecked == true,
            AoStrength = Round(AoStrengthSlider),
            AoRadius = Round(AoRadiusSlider),
            SsrEnabled = SsrCheck.IsChecked == true,
            SsrStrength = Round(SsrStrengthSlider),
            SsrDistance = (float)Math.Round(SsrDistanceSlider.Value, 0),
            SsrRayTracing = SsrRayTracingCheck.IsChecked == true,
            DofEnabled = DofCheck.IsChecked == true,
            DofFocusOnTarget = FocusOnTargetCheck.IsChecked == true,
            DofFocusDistance = Round(FocusDistanceSlider),
            DofFocusRange = Round(FocusRangeSlider),
            DofBlur = Round(DofBlurSlider),
        };
    }

    private void UpdateTexts()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        ExposureText.Text = ExposureSlider.Value.ToString("+0.00;-0.00;0.00", c) + " 段";
        ContrastText.Text = ContrastSlider.Value.ToString("0.00", c);
        SaturationText.Text = SaturationSlider.Value.ToString("0.00", c);
        TemperatureText.Text = TemperatureSlider.Value.ToString("+0.00;-0.00;0.00", c);
        BloomIntensityText.Text = BloomIntensitySlider.Value.ToString("0.00", c);
        BloomThresholdText.Text = BloomThresholdSlider.Value.ToString("0.00", c);
        BloomSpreadText.Text = BloomSpreadSlider.Value.ToString("0.00", c);
        VignetteText.Text = VignetteSlider.Value.ToString("0.00", c);
        AoStrengthText.Text = AoStrengthSlider.Value.ToString("0.00", c);
        AoRadiusText.Text = AoRadiusSlider.Value.ToString("0.00", c);
        SsrStrengthText.Text = SsrStrengthSlider.Value.ToString("0.00", c);
        SsrDistanceText.Text = SsrDistanceSlider.Value.ToString("0", c);
        FocusDistanceText.Text = FocusDistanceSlider.Value.ToString("0.0", c);
        FocusRangeText.Text = FocusRangeSlider.Value.ToString("0.0", c);
        DofBlurText.Text = DofBlurSlider.Value.ToString("0.0", c);
        FocusDistanceSlider.IsEnabled = FocusOnTargetCheck.IsChecked != true; // 注視点に合わせるときは使わない
    }

    private void Apply()
    {
        if (_loading || _settings is null)
        {
            return; // 作っている途中（InitializeComponent の中）と、値を出している間は入れない
        }
        UpdateTexts();
        _settings.Effects = Read();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ShowValues(VisualEffects.None);
        if (_settings is not null)
        {
            _settings.Effects = VisualEffects.None;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
