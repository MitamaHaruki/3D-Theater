namespace Theater3D.Formats;

public enum LoadSeverity
{
    /// <summary>知らせるだけ（そのまま使える）。</summary>
    Info,
    /// <summary>読み込めたが、見た目や動きが元と違う可能性がある。</summary>
    Warning,
    /// <summary>読み込めなかった。</summary>
    Error,
}

public enum LoadIssueCategory
{
    /// <summary>ファイルの形式そのものの問題（壊れている・違う形式など）。</summary>
    Format,
    /// <summary>3D Theater がまだ対応していない要素。</summary>
    Unsupported,
    /// <summary>存在しない頂点・ボーンなどを指している。</summary>
    InvalidReference,
    /// <summary>テクスチャなどの素材ファイルが見つからない。</summary>
    MissingFile,
    /// <summary>補足。</summary>
    Note,
}

public sealed record LoadIssue(LoadSeverity Severity, LoadIssueCategory Category, string Message)
{
    public string SeverityText => Severity switch
    {
        LoadSeverity.Error => "エラー",
        LoadSeverity.Warning => "注意",
        _ => "情報",
    };

    public string CategoryText => Category switch
    {
        LoadIssueCategory.Format => "形式",
        LoadIssueCategory.Unsupported => "未対応",
        LoadIssueCategory.InvalidReference => "参照の誤り",
        LoadIssueCategory.MissingFile => "素材が見つからない",
        _ => "補足",
    };
}

/// <summary>
/// 読み込みレポート。読み込みに成功したかどうかと、「読めたが元と違う可能性がある点」を利用者に知らせる（指示書 4.1）。
/// 「読み込めた」ことと「MMD と同じ見た目・動きになる」ことは別に確かめる前提で、後者の保証はしない。
/// </summary>
public sealed class LoadReport
{
    private readonly List<LoadIssue> _issues = [];

    public IReadOnlyList<LoadIssue> Issues => _issues;

    public bool HasErrors => _issues.Any(i => i.Severity == LoadSeverity.Error);

    public int WarningCount => _issues.Count(i => i.Severity == LoadSeverity.Warning);

    public void Add(LoadSeverity severity, LoadIssueCategory category, string message) =>
        _issues.Add(new LoadIssue(severity, category, message));

    public void Error(LoadIssueCategory category, string message) => Add(LoadSeverity.Error, category, message);

    public void Warning(LoadIssueCategory category, string message) => Add(LoadSeverity.Warning, category, message);

    public void Info(LoadIssueCategory category, string message) => Add(LoadSeverity.Info, category, message);

    /// <summary>同じ種類の問題が多数あるとき、件数と最初のいくつかの例だけを 1 行にまとめて載せる。</summary>
    public void AddAggregated(LoadSeverity severity, LoadIssueCategory category, string what, IReadOnlyCollection<string> examples, int total)
    {
        if (total <= 0)
        {
            return;
        }
        string sample = string.Join("、", examples.Take(3));
        string more = total > 3 ? " ほか" : string.Empty;
        Add(severity, category, $"{what}: {total} 件（例: {sample}{more}）");
    }
}

/// <summary>ファイルの形式の誤りで読み込めないとき。</summary>
public sealed class ModelFormatException(string message) : Exception(message);
