using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// イラスト風の表現の設定（レンダリングのメニュー）。線画・塗り・影の色・紙の質感を ON / OFF し、値を変える。
/// 変えるたびに作品の描き方（<see cref="RenderSettings.Illustration"/>）へ入れるので、すぐにプレビューへ反映し、プロジェクトは変更ありになる。
/// 色は #RRGGBB で入れ、読めない間は前の色のままにする。
/// </summary>
public partial class IllustrationWindow : Window, IShowsCurrentValues
{
    private readonly RenderSettings? _settings;
    private bool _loading;

    internal IllustrationWindow(RenderSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ShowValues(settings.Illustration);
    }

    /// <summary>設定の今の値を出し直す（ライブモードの設定画面で、ほかの所で変わったとき）。</summary>
    public void ShowCurrentValues()
    {
        if (_settings is not null)
        {
            ShowValues(_settings.Illustration);
        }
    }

    private void ShowValues(IllustrationStyle style)
    {
        _loading = true;
        try
        {
            LinesCheck.IsChecked = style.LinesEnabled;
            LineWidthSlider.Value = style.LineWidth;
            LineStrengthSlider.Value = style.LineStrength;
            LineDetailSlider.Value = style.LineDetail;
            LineColorBox.Text = style.LineColor;
            PaintCheck.IsChecked = style.PaintEnabled;
            PaintLevelsSlider.Value = style.PaintLevels;
            ShadowTintCheck.IsChecked = style.ShadowTintEnabled;
            ShadowColorBox.Text = style.ShadowColor;
            ShadowTintSlider.Value = style.ShadowTintStrength;
            PaperCheck.IsChecked = style.PaperEnabled;
            PaperStrengthSlider.Value = style.PaperStrength;
            PaperGrainSlider.Value = style.PaperGrain;
        }
        finally
        {
            _loading = false;
        }
        UpdateTexts();
    }

    /// <summary>画面の値（小数 2 桁に丸める）。読めない色は、今の設定の色のままにする。</summary>
    private IllustrationStyle Read(IllustrationStyle current)
    {
        static float Round(Slider slider) => (float)Math.Round(slider.Value, 2);
        return new IllustrationStyle
        {
            LinesEnabled = LinesCheck.IsChecked == true,
            LineWidth = Round(LineWidthSlider),
            LineStrength = Round(LineStrengthSlider),
            LineDetail = Round(LineDetailSlider),
            LineColor = ImageExport.TryParseColor(LineColorBox.Text, out _) ? LineColorBox.Text.Trim() : current.LineColor,
            PaintEnabled = PaintCheck.IsChecked == true,
            PaintLevels = (int)Math.Round(PaintLevelsSlider.Value),
            ShadowTintEnabled = ShadowTintCheck.IsChecked == true,
            ShadowColor = ImageExport.TryParseColor(ShadowColorBox.Text, out _) ? ShadowColorBox.Text.Trim() : current.ShadowColor,
            ShadowTintStrength = Round(ShadowTintSlider),
            PaperEnabled = PaperCheck.IsChecked == true,
            PaperStrength = Round(PaperStrengthSlider),
            PaperGrain = Round(PaperGrainSlider),
        };
    }

    private void UpdateTexts()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        LineWidthText.Text = LineWidthSlider.Value.ToString("0.00", c) + " 画素";
        LineStrengthText.Text = LineStrengthSlider.Value.ToString("0.00", c);
        LineDetailText.Text = LineDetailSlider.Value.ToString("0.00", c);
        PaintLevelsText.Text = ((int)Math.Round(PaintLevelsSlider.Value)).ToString(c) + " 段";
        ShadowTintText.Text = ShadowTintSlider.Value.ToString("0.00", c);
        PaperStrengthText.Text = PaperStrengthSlider.Value.ToString("0.00", c);
        PaperGrainText.Text = PaperGrainSlider.Value.ToString("0.0", c) + " 画素";
        UpdateSwatch(LineColorBox, LineColorSwatch);
        UpdateSwatch(ShadowColorBox, ShadowColorSwatch);
    }

    private static void UpdateSwatch(TextBox box, System.Windows.Controls.Border swatch)
    {
        swatch.Background = ImageExport.TryParseColor(box.Text, out Vector3 color)
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
        _settings.Illustration = Read(_settings.Illustration);
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnColorChanged(object sender, TextChangedEventArgs e) => Apply();

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ShowValues(IllustrationStyle.None);
        if (_settings is not null)
        {
            _settings.Illustration = IllustrationStyle.None;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
