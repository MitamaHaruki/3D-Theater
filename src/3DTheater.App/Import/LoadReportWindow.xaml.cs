using System.Text;
using System.Windows;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.App.Import;

/// <summary>読み込みレポートを一覧で見せる窓。内容をコピーして人に伝えられるようにする。</summary>
public partial class LoadReportWindow : Window
{
    private readonly string _path;
    private readonly LoadReport _report;
    private readonly ModelSummary? _summary;

    private LoadReportWindow(string path, LoadReport report, ModelSummary? summary)
    {
        InitializeComponent();
        _path = path;
        _report = report;
        _summary = summary;

        int warnings = report.WarningCount;
        HeadlineText.Text = report.HasErrors
            ? "モデルを読み込めませんでした"
            : warnings > 0 ? $"読み込みました（注意 {warnings} 件）" : "読み込みました（問題はありません）";
        PathText.Text = path;
        SummaryText.Text = summary is null ? string.Empty : FormatSummary(summary);
        // エラー → 注意 → 情報 の順に並べる
        IssueList.ItemsSource = report.Issues.OrderByDescending(i => i.Severity).ToList();
    }

    public static void Show(Window? owner, string path, LoadReport report, ModelSummary? summary)
    {
        var window = new LoadReportWindow(path, report, summary) { Owner = owner };
        window.ShowDialog();
    }

    private static string FormatSummary(ModelSummary s) =>
        $"{s.Format}　頂点 {s.Vertices:N0}・面 {s.Faces:N0}・材質 {s.Materials:N0}・テクスチャ {s.Textures:N0}・" +
        $"ボーン {s.Bones:N0}・モーフ {s.Morphs:N0}・剛体 {s.RigidBodies:N0}・ジョイント {s.Joints:N0}";

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        text.AppendLine(HeadlineText.Text);
        text.AppendLine(_path);
        if (_summary is not null)
        {
            text.AppendLine(FormatSummary(_summary));
        }
        foreach (LoadIssue issue in _report.Issues.OrderByDescending(i => i.Severity))
        {
            text.AppendLine($"[{issue.SeverityText}] {issue.CategoryText}: {issue.Message}");
        }
        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // ほかのアプリがクリップボードを使っているときは失敗することがある
            AppLog.Warning("レポートをクリップボードにコピーできませんでした", ex);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
