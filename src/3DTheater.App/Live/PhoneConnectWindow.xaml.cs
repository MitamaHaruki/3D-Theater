using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using Theater3D.Editor;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// スマホから受け取るときに出す窓（設計判断記録 78 章の 5 段目。前はトラッキングのパネルの中にあった欄）。
/// 頼む形（VTube Studio・MeowFace）はスマホの IP を入れる。送ってもらう形（iFacialMocap など）は、この PC の IP をスマホに入れる。
/// 「接続」を押したときだけ、入れた値を設定（ライブのファイルに保存する）へ入れる。
/// </summary>
public partial class PhoneConnectWindow : Window
{
    private readonly PhoneTrackingSettings _settings;
    private readonly bool _request;

    /// <param name="request">true なら頼む形（スマホの IP を入れる）、false なら送ってもらう形。</param>
    internal PhoneConnectWindow(PhoneTrackingSettings settings, bool request)
    {
        InitializeComponent();
        _settings = settings;
        _request = request;
        string addresses = string.Join("・", LocalAddresses());
        if (request)
        {
            KindText.Text = "スマホに頼んで受け取る（VTube Studio・MeowFace）";
            HelpText.Text = "iPhone の VTube Studio は、設定の「3rd Party PC Clients」を入れ、アプリに出ている IP を下に入れて「接続」します。"
                + $"Android の MeowFace も、アプリに出ている IP を入れます。この PC はポート {PhoneMessages.VTubeStudioReplyPort} で受け取ります（PC とスマホは同じ Wi-Fi に）。";
        }
        else
        {
            KindText.Text = "スマホから送ってもらう（iFacialMocap・FaceMotion3D・MeowFace）";
            HelpText.Text = $"スマホのアプリの送り先に、この PC の IP（{(addresses.Length > 0 ? addresses : "見つかりません")}）を入れてから「接続」します。"
                + $"受け取るポートは {PhoneMessages.IFacialMocapPort}（iFacialMocap・FaceMotion3D）と {PhoneMessages.MeowFacePort}（MeowFace）です（PC とスマホは同じ Wi-Fi に）。";
            AddressRow.Visibility = Visibility.Collapsed;
        }
        AddressBox.Text = settings.Address;
        InvertYawCheck.IsChecked = settings.InvertYaw;
        InvertPitchCheck.IsChecked = settings.InvertPitch;
        InvertRollCheck.IsChecked = settings.InvertRoll;
        Loaded += (_, _) =>
        {
            if (request)
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
            }
            else
            {
                ConnectButton.Focus();
            }
        };
    }

    /// <summary>頼む先のスマホ（頼む形で「接続」したとき）。</summary>
    public IPAddress? Phone { get; private set; }

    private void OnAddressChanged(object sender, TextChangedEventArgs e) => ProblemText.Text = string.Empty;

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        IPAddress? phone = null;
        if (_request && !IPAddress.TryParse(AddressBox.Text.Trim(), out phone))
        {
            ProblemText.Text = "スマホのアプリに出ている IP（例: 192.168.1.20）を入れてください";
            AddressBox.Focus();
            return;
        }
        if (_request)
        {
            _settings.Address = AddressBox.Text.Trim();
        }
        _settings.InvertYaw = InvertYawCheck.IsChecked == true;
        _settings.InvertPitch = InvertPitchCheck.IsChecked == true;
        _settings.InvertRoll = InvertRollCheck.IsChecked == true;
        Phone = phone;
        DialogResult = true;
    }

    // この PC の IPv4 の住所（家の Wi-Fi・有線のもの）
    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                    && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
