using System.Globalization;
using System.Windows;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 影の設定（レンダリングのメニュー）。影のやわらかさ（セルフシャドウと地面の影）と地面の影の濃さを変える。
/// 変えるたびに作品の描き方（<see cref="RenderSettings.ShadowStyle"/>）へ入れるので、すぐにプレビューへ反映し、プロジェクトは変更ありになる。
/// </summary>
public partial class ShadowWindow : Window
{
    private readonly RenderSettings? _settings;
    private bool _loading;

    internal ShadowWindow(RenderSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ShowValues(settings.ShadowStyle);
    }

    private void ShowValues(ShadowStyle style)
    {
        _loading = true;
        try
        {
            SoftnessSlider.Value = style.Softness;
            GroundOpacitySlider.Value = style.GroundOpacity;
        }
        finally
        {
            _loading = false;
        }
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        SoftnessText.Text = SoftnessSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        GroundOpacityText.Text = GroundOpacitySlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _settings is null)
        {
            return; // 作っている途中（InitializeComponent の中）と、値を出している間は入れない
        }
        UpdateTexts();
        _settings.ShadowStyle = new ShadowStyle
        {
            Softness = (float)Math.Round(SoftnessSlider.Value, 2),
            GroundOpacity = (float)Math.Round(GroundOpacitySlider.Value, 2),
        };
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ShowValues(ShadowStyle.Default);
        if (_settings is not null)
        {
            _settings.ShadowStyle = ShadowStyle.Default;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
