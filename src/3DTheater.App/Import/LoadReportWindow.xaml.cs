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

    private LoadReportWindow(string path, LoadReport report, ModelSummary? summary, string what, bool isExport)
    {
        InitializeComponent();
        _path = path;
        _report = report;
        _summary = summary;

        int warnings = report.WarningCount;
        string done = isExport ? "書き出しました" : "読み込みました";
        HeadlineText.Text = report.HasErrors
            ? isExport ? $"{what}を書き出せませんでした" : $"{what}を読み込めませんでした"
            : warnings > 0 ? $"{done}（注意 {warnings} 件）" : $"{done}（問題はありません）";
        Title = isExport ? "書き出しのレポート" : "読み込みレポート";
        PathText.Text = path;
        SummaryText.Text = summary is null ? string.Empty : FormatSummary(summary);
        // エラー → 注意 → 情報 の順に並べる
        IssueList.ItemsSource = report.Issues.OrderByDescending(i => i.Severity).ToList();
    }

    /// <param name="what">読み込んだものの呼び名（「モデル」「モーション」「ポーズ」「プロジェクト」）。</param>
    /// <param name="isExport">書き出しのレポートか。</param>
    public static void Show(Window? owner, string path, LoadReport report, ModelSummary? summary, string what = "モデル", bool isExport = false)
    {
        var window = new LoadReportWindow(path, report, summary, what, isExport) { Owner = owner };
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
