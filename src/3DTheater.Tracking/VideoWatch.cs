namespace Theater3D.Tracking;

/// <summary>
/// カメラから映像が届いているかの見張りと、開き直しの決まり（設計判断記録 97 章）。
/// <list type="bullet">
/// <item>開いてから・最後に届いてから <see cref="NoticeAfter"/> たっても届かなければ、知らせる（<see cref="Problem"/>）。</item>
/// <item><see cref="StallAfter"/> たっても届かなければ、止まったとみなして開き直す（<see cref="IsStalled"/>。読み込みは見張る側がやめさせる）。</item>
/// <item>読み出しに失敗した・止まったときは、<see cref="RetryDelays"/> の間をあけて開き直す。続けて失敗したら、全部を試した後にやめる。
/// 映像が 1 枚でも届けば、数え直す。</item>
/// </list>
/// 時刻は、決まった起点からの経過（<see cref="TimeSpan"/>）で渡す。読み込み用のスレッドと画面のスレッドの両方から呼んでよい。
/// </summary>
public sealed class VideoWatch
{
    /// <summary>映像が届かないことを知らせるまでの時間。</summary>
    public static TimeSpan NoticeAfter { get; } = TimeSpan.FromSeconds(3);

    /// <summary>映像が届かないときに、止まったとみなして開き直すまでの時間。</summary>
    public static TimeSpan StallAfter { get; } = TimeSpan.FromSeconds(15);

    /// <summary>続けて失敗したときの、開き直すまでの間（この回数まで開き直す）。</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly object _lock = new();
    private TimeSpan _since;
    private bool _watching;
    private bool _opening;
    private int _failures;
    private string? _lastError;
    private TimeSpan? _retryAt;

    /// <summary>続けて失敗した回数（映像が届くと 0 に戻る）。</summary>
    public int Failures
    {
        get
        {
            lock (_lock)
            {
                return _failures;
            }
        }
    }

    /// <summary>見張りを始める（トラッキングを始めたとき）。失敗の数も 0 に戻す。</summary>
    public void Reset(TimeSpan now)
    {
        lock (_lock)
        {
            _since = now;
            _watching = true;
            _opening = true;
            _failures = 0;
            _lastError = null;
            _retryAt = null;
        }
    }

    /// <summary>見張りをやめる（トラッキングを止めたとき・スマホから受け取るとき）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _watching = false;
            _retryAt = null;
        }
    }

    /// <summary>カメラを開き始めた（開き直すときも）。</summary>
    public void Opening(TimeSpan now)
    {
        lock (_lock)
        {
            _since = now;
            _opening = true;
            _retryAt = null;
        }
    }

    /// <summary>カメラを開き終えた（ここから届くのを待つ。顔のモデルを読む時間などは数えない）。</summary>
    public void Opened(TimeSpan now)
    {
        lock (_lock)
        {
            _since = now;
            _opening = false;
        }
    }

    /// <summary>映像が 1 枚届いた。</summary>
    public void FrameArrived(TimeSpan now)
    {
        lock (_lock)
        {
            _since = now;
            _opening = false;
            _failures = 0;
            _lastError = null;
        }
    }

    /// <summary>届かないまま <see cref="StallAfter"/> たったか（開いている途中は数えない。開いている途中では、やめさせられないため）。</summary>
    public bool IsStalled(TimeSpan now)
    {
        lock (_lock)
        {
            return _watching && !_opening && _retryAt is null && now - _since >= StallAfter;
        }
    }

    /// <summary>
    /// 開く・読むのに失敗した（<paramref name="error"/> は理由）。開き直すまで待つ時間を返す。もう開き直さないなら null。
    /// </summary>
    public TimeSpan? Failed(string error, TimeSpan now)
    {
        lock (_lock)
        {
            _failures++;
            _lastError = error;
            if (_failures > RetryDelays.Count)
            {
                _retryAt = null;
                return null;
            }
            TimeSpan delay = RetryDelays[_failures - 1];
            _retryAt = now + delay;
            return delay;
        }
    }

    /// <summary>
    /// 今の困りごとの知らせ（無ければ null）。<paramref name="source"/> は映像の名前（カメラの名前など）。
    /// 開き直すのを待っている間・開き直している間・映像が <see cref="NoticeAfter"/> より長く届かない間に出す。
    /// </summary>
    public string? Problem(TimeSpan now, string? source)
    {
        lock (_lock)
        {
            if (!_watching)
            {
                return null;
            }
            string name = string.IsNullOrEmpty(source) ? "カメラ" : $"「{source}」";
            if (_retryAt is { } at)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling((at - now).TotalSeconds));
                return $"{name}の映像を読めません（{_lastError}）。{seconds} 秒後に開き直します（{_failures}/{RetryDelays.Count} 回目）";
            }
            TimeSpan silence = now - _since;
            if (_failures > 0 && _failures <= RetryDelays.Count)
            {
                return $"{name}を開き直しています（{_failures}/{RetryDelays.Count} 回目）…";
            }
            if (silence >= NoticeAfter)
            {
                string what = _opening ? "が開きません" : "から映像が届きません";
                return $"{name}{what}（{(int)silence.TotalSeconds} 秒）。ほかのアプリがカメラを使っていないか、Windows の設定でカメラを使えるようになっているかを確かめてください";
            }
            return null;
        }
    }
}
