using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Theater3D.App.Export;

/// <summary>
/// 書き出しの見本を見せる画面。書き出しと同じ設定で描いた 1 枚を、窓に合わせて（または実際の大きさで）見せる。
/// 透明な所は市松模様で見せる。
/// </summary>
public partial class SampleWindow : Window
{
    private readonly BitmapSource _image;

    internal SampleWindow(BitmapSource image, string description, bool transparent)
    {
        InitializeComponent();
        _image = image;
        SampleImage.Source = image;
        Checker.Visibility = transparent ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Text = description;
        ApplyScale();
    }

    private void ApplyScale()
    {
        bool actual = ActualSizeCheck.IsChecked == true;
        if (actual)
        {
            // 1 画素を画面の 1 画素で見せる（拡大率のある画面では、WPF の単位で小さくする）
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            SampleImage.Stretch = Stretch.None;
            ImageArea.Width = _image.PixelWidth / scale;
            ImageArea.Height = _image.PixelHeight / scale;
            SampleImage.Width = ImageArea.Width;
            SampleImage.Height = ImageArea.Height;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else
        {
            SampleImage.Stretch = Stretch.Uniform;
            ImageArea.Width = double.NaN;
            ImageArea.Height = double.NaN;
            SampleImage.Width = double.NaN;
            SampleImage.Height = double.NaN;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private void OnScaleChanged(object sender, RoutedEventArgs e)
    {
        if (_image is not null)
        {
            ApplyScale();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
