using System.Globalization;
using System.Runtime.InteropServices;

namespace Theater3D.Editor;

/// <summary>
/// かかった時間（ミリ秒）の分かれ方。0.25 ms 刻みの箱で数え（250 ms より長いものは最後の箱）、平均・95% の値・最大を求める。
/// 足した数によらず、使うメモリは変わらない（長く動かしても増えない）。ロックしないので、ほかのスレッドと分けて使うときは使う側でロックする。
/// </summary>
public sealed class TimeHistogram
{
    public const double BucketMilliseconds = 0.25;
    public const int BucketCount = 1000;

    private readonly long[] _buckets = new long[BucketCount + 1];
    private double _sum;

    public long Count { get; private set; }

    public double Max { get; private set; }

    public double Mean => Count == 0 ? 0 : _sum / Count;

    public void Add(double milliseconds)
    {
        if (double.IsNaN(milliseconds))
        {
            return;
        }
        milliseconds = Math.Max(0, milliseconds);
        int index = milliseconds >= BucketCount * BucketMilliseconds ? BucketCount : (int)(milliseconds / BucketMilliseconds);
        _buckets[index]++;
        Count++;
        _sum += milliseconds;
        Max = Math.Max(Max, milliseconds);
    }

    /// <summary>全体の <paramref name="fraction"/>（0〜1）が、この時間までに収まる（箱の上の端。最大は超えない）。</summary>
    public double Percentile(double fraction)
    {
        if (Count == 0)
        {
            return 0;
        }
        long rank = Math.Max(1, (long)Math.Ceiling(Math.Clamp(fraction, 0, 1) * Count));
        long seen = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            seen += _buckets[i];
            if (seen >= rank)
            {
                return i == BucketCount ? Max : Math.Min(Max, (i + 1) * BucketMilliseconds); // 最後の箱は、上の端が無い
            }
        }
        return Max;
    }

    public void Merge(TimeHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] += other._buckets[i];
        }
        Count += other.Count;
        _sum += other._sum;
        Max = Math.Max(Max, other.Max);
    }

    public void Clear()
    {
        Array.Clear(_buckets);
        Count = 0;
        _sum = 0;
        Max = 0;
    }

    public TimeSummary Summarize() => new(Count, Mean, Percentile(0.95), Max);
}

/// <summary>かかった時間のまとめ（ミリ秒。数・平均・95% の値・最大）。</summary>
public readonly record struct TimeSummary(long Count, double Mean, double P95, double Max);

/// <summary>顔のトラッキングの、ある間の速さ（<see cref="Duration"/> は、トラッキングを動かしていた時間）。</summary>
public sealed record TrackingPeriod(bool Phone, TimeSpan Duration, long Frames, long Found, long Detected, TimeSummary Process, TimeSummary Wait)
{
    public double FramesPerSecond => Duration > TimeSpan.Zero ? Frames / Duration.TotalSeconds : 0;

    /// <summary>顔を見つけた割合（0〜1）。</summary>
    public double FoundRatio => Frames == 0 ? 0 : (double)Found / Frames;

    /// <summary>この間、トラッキングを動かしていなかった。</summary>
    public bool IsEmpty => Duration <= TimeSpan.Zero && Frames == 0;
}

/// <summary>
/// 顔のトラッキングの速さを数える（ライブモードの速さのログ。設計判断記録 77 章）。読み込み用のスレッドが 1 枚ごとに足し、画面のスレッドが区切って読む。
/// 回数の割り算に使う時間は、トラッキングを動かしていた間だけを数える（止めていた間は入れない）。
/// 「1 回分」（始めてから止めるまで）・「区切り」（前に読んでから）・「全体」（作ってから）の 3 つを数える。
/// </summary>
public sealed class TrackingStats
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Accumulator _total = new();
    private Accumulator _interval = new();
    private Accumulator _run = new();
    private bool _running;
    private long _mark;

    public TrackingStats(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>始めた（映像を開けた・受け取りを始めた）。前の 1 回分が終わっていなければ、そこで区切る。</summary>
    public void Begin(bool phone)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (_running)
            {
                Credit(now);
            }
            _running = true;
            _mark = now;
            _run = new Accumulator { Phone = phone };
            _interval.Phone = phone;
            _total.Phone = phone;
        }
    }

    /// <summary>カメラ（動画のファイル）の 1 枚を読んだ（処理の時間・映像を待った時間・顔を見つけたか・顔を探し直したか）。</summary>
    public void AddCameraFrame(double processMilliseconds, double waitMilliseconds, bool found, bool detected)
    {
        lock (_gate)
        {
            _interval.AddCamera(processMilliseconds, waitMilliseconds, found, detected);
            _run.AddCamera(processMilliseconds, waitMilliseconds, found, detected);
            _total.AddCamera(processMilliseconds, waitMilliseconds, found, detected);
        }
    }

    /// <summary>スマホから 1 つ受け取った（顔があったか）。</summary>
    public void AddPhoneFrame(bool found)
    {
        lock (_gate)
        {
            _interval.AddPhone(found);
            _run.AddPhone(found);
            _total.AddPhone(found);
        }
    }

    /// <summary>止めた。その 1 回分を返す（始めていなければ null）。</summary>
    public TrackingPeriod? Finish()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return null;
            }
            Credit(_time.GetTimestamp());
            _running = false;
            return _run.ToPeriod();
        }
    }

    /// <summary>前に読んでから今までの分を返し、数え直す。</summary>
    public TrackingPeriod TakeInterval()
    {
        lock (_gate)
        {
            if (_running)
            {
                Credit(_time.GetTimestamp());
            }
            TrackingPeriod period = _interval.ToPeriod();
            _interval = new Accumulator { Phone = _interval.Phone };
            return period;
        }
    }

    /// <summary>作ってから今までの分。</summary>
    public TrackingPeriod Total
    {
        get
        {
            lock (_gate)
            {
                if (_running)
                {
                    Credit(_time.GetTimestamp());
                }
                return _total.ToPeriod();
            }
        }
    }

    private void Credit(long now)
    {
        TimeSpan elapsed = _time.GetElapsedTime(_mark, now);
        _interval.Active += elapsed;
        _run.Active += elapsed;
        _total.Active += elapsed;
        _mark = now;
    }

    private sealed class Accumulator
    {
        public bool Phone { get; set; }

        public TimeSpan Active { get; set; }

        private readonly TimeHistogram _process = new();
        private readonly TimeHistogram _wait = new();
        private long _frames;
        private long _found;
        private long _detected;

        public void AddCamera(double processMilliseconds, double waitMilliseconds, bool found, bool detected)
        {
            _frames++;
            _found += found ? 1 : 0;
            _detected += detected ? 1 : 0;
            _process.Add(processMilliseconds);
            _wait.Add(waitMilliseconds);
        }

        public void AddPhone(bool found)
        {
            _frames++;
            _found += found ? 1 : 0;
        }

        public TrackingPeriod ToPeriod() => new(Phone, Active, _frames, _found, _detected, _process.Summarize(), _wait.Summarize());
    }
}

/// <summary>描画の、ある間の速さ。</summary>
public sealed record RenderPeriod(TimeSpan Duration, long Ticks, long Frames, long Hitches, long Pauses, double LongestStep, TimeSummary Frame, TimeSummary Gpu)
{
    public double FramesPerSecond => Duration > TimeSpan.Zero ? Frames / Duration.TotalSeconds : 0;
}

/// <summary>
/// 描画の速さを数える（ライブモードの速さのログ）。描画の繰り返し（UI スレッド）が足し、同じスレッドで区切って読む。
/// 画面の更新の間が <see cref="HitchMilliseconds"/> 以上空いたら「つかえ」、<see cref="PauseMilliseconds"/> 以上なら「止まった」と数える
/// （窓を小さくしたときなど。つかえには入れない）。
/// </summary>
public sealed class RenderStats
{
    public const double HitchMilliseconds = 50;
    public const double PauseMilliseconds = 1000;

    private readonly TimeProvider _time;
    private readonly Accumulator _total;
    private Accumulator _interval;

    public RenderStats(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        long now = _time.GetTimestamp();
        _total = new Accumulator(now);
        _interval = new Accumulator(now);
    }

    /// <summary>画面の更新が 1 回来た（前の更新からの時間）。</summary>
    public void AddTick(double stepMilliseconds)
    {
        _interval.AddTick(stepMilliseconds);
        _total.AddTick(stepMilliseconds);
    }

    /// <summary>1 回描いた（UI スレッドの時間・GPU が描くのに掛けた時間（測れたときだけ））。</summary>
    public void AddFrame(double milliseconds, double? gpuMilliseconds)
    {
        _interval.AddFrame(milliseconds, gpuMilliseconds);
        _total.AddFrame(milliseconds, gpuMilliseconds);
    }

    /// <summary>前に読んでから今までの分を返し、数え直す。</summary>
    public RenderPeriod TakeInterval()
    {
        long now = _time.GetTimestamp();
        RenderPeriod period = _interval.ToPeriod(_time, now);
        _interval = new Accumulator(now);
        return period;
    }

    /// <summary>作ってから今までの分。</summary>
    public RenderPeriod Total => _total.ToPeriod(_time, _time.GetTimestamp());

    private sealed class Accumulator(long start)
    {
        private readonly TimeHistogram _frame = new();
        private readonly TimeHistogram _gpu = new();
        private long _ticks;
        private long _hitches;
        private long _pauses;
        private double _longest;

        public void AddTick(double stepMilliseconds)
        {
            _ticks++;
            if (stepMilliseconds >= PauseMilliseconds)
            {
                _pauses++;
                return;
            }
            if (stepMilliseconds >= HitchMilliseconds)
            {
                _hitches++;
            }
            _longest = Math.Max(_longest, stepMilliseconds);
        }

        public void AddFrame(double milliseconds, double? gpuMilliseconds)
        {
            _frame.Add(milliseconds);
            if (gpuMilliseconds is double gpu)
            {
                _gpu.Add(gpu);
            }
        }

        public RenderPeriod ToPeriod(TimeProvider time, long now) =>
            new(time.GetElapsedTime(start, now), _ticks, _frame.Count, _hitches, _pauses, _longest, _frame.Summarize(), _gpu.Summarize());
    }
}

/// <summary>
/// メモリの量の移り変わり（1 分ごとの値）。始め・いちばん少ないとき・いちばん多いとき・終わりと、増え方（1 時間あたり。最小二乗の直線の傾き）を求める。
/// 起動した直後はモデルの読み込みなどで増えるので、増え方は <see cref="WarmUp"/> より後の値で求める。値は持たずに和だけを持つ（長く動かしても増えない）。
/// メモリは 1 分ごとに数十 MB 上下することがあり（ノートパソコンで ±25 MB ほど）、短い間の値で傾きを求めると、上下を「1 時間あたり」に
/// 引き伸ばして大きく見せてしまう（5 分で +390 MB/時と出た。±14 MB の上下なら、5 分の傾きは ±370 MB/時ほど、20 分なら ±30 MB/時ほどぶれる）。
/// そのため、<see cref="WarmUp"/> より後に <see cref="MinimumSpan"/> 以上の値がそろうまで求めない。
/// </summary>
public sealed class MemoryTrend
{
    public static readonly TimeSpan WarmUp = TimeSpan.FromMinutes(2);

    /// <summary>増え方を求めるのに要る、<see cref="WarmUp"/> より後の値の数。</summary>
    public const int MinimumSamples = 3;

    /// <summary>増え方を求めるのに要る、<see cref="WarmUp"/> より後の値の時間の幅。</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(20);

    /// <summary>増え方が分かるまでに動かす時間（起動してから）。</summary>
    public static TimeSpan TimeNeeded => WarmUp + MinimumSpan;

    private double? _origin;
    private double _lastX;
    private int _n;
    private double _sumX;
    private double _sumY;
    private double _sumXy;
    private double _sumXx;

    public double? First { get; private set; }

    public double? Lowest { get; private set; }

    public double? Peak { get; private set; }

    public double? Last { get; private set; }

    /// <summary>増え方を求め始めた時刻（<see cref="WarmUp"/> より後の、初めの値の時刻）。</summary>
    public TimeSpan? GrowthFrom { get; private set; }

    public void Add(TimeSpan at, double megabytes)
    {
        First ??= megabytes;
        Lowest = Math.Min(Lowest ?? megabytes, megabytes);
        Peak = Math.Max(Peak ?? megabytes, megabytes);
        Last = megabytes;
        if (at < WarmUp)
        {
            return;
        }
        GrowthFrom ??= at;
        _origin ??= at.TotalHours;
        double x = at.TotalHours - _origin.Value; // 小さな数にしてから掛ける（桁落ちを防ぐ）
        _lastX = x;
        _n++;
        _sumX += x;
        _sumY += megabytes;
        _sumXy += x * megabytes;
        _sumXx += x * x;
    }

    /// <summary>1 時間あたりの増え方（MB）。値が足りなければ null。</summary>
    public double? GrowthPerHour
    {
        get
        {
            if (_n < MinimumSamples || _lastX < MinimumSpan.TotalHours - 1e-9)
            {
                return null;
            }
            double denominator = (_n * _sumXx) - (_sumX * _sumX);
            return denominator <= 0 ? null : ((_n * _sumXy) - (_sumX * _sumY)) / denominator;
        }
    }
}

/// <summary>電源の様子（電池か・電池の残り・省エネ機能・電源モード）。</summary>
public sealed record PowerState(bool HasBattery, bool? OnBattery, int? BatteryPercent, bool EnergySaver, Guid? Mode)
{
    /// <summary>電源モード「最適な電力効率」。</summary>
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>電源モード「バランス」。</summary>
    public static readonly Guid Balanced = Guid.Empty;

    /// <summary>電源モード「最適なパフォーマンス」。</summary>
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>電源モードの名前（Windows の設定の表示と同じ）。読めなければ null。</summary>
    public static string? ModeName(Guid? mode) => mode switch
    {
        null => null,
        { } g when g == BestEfficiency => "最適な電力効率",
        { } g when g == Balanced => "バランス",
        { } g when g == BestPerformance => "最適なパフォーマンス",
        { } g => $"そのほか {g}",
    };

    public string Describe()
    {
        var parts = new List<string>(3);
        string battery = BatteryPercent is int percent ? $"電池 {percent}%" : "電池";
        parts.Add(OnBattery switch
        {
            true => battery,
            false when HasBattery => $"コンセント（{battery}）",
            false => "コンセント",
            null => "電源 不明",
        });
        if (EnergySaver)
        {
            parts.Add("省エネ機能 オン");
        }
        if (ModeName(Mode) is { } name)
        {
            parts.Add($"電源モード「{name}」");
        }
        return string.Join("・", parts);
    }
}

/// <summary>仮想カメラの、ある間の様子（描いた回数と、読んだ時にほかのアプリが使っているか）。</summary>
public readonly record struct VirtualCameraPeriod(double FramesPerSecond, bool InUse);

/// <summary>ある時の重さ（CPU・メモリ・GPU のメモリ・電源）。</summary>
public sealed record LoadSample(
    double CpuPercent,
    double PrivateMegabytes,
    double WorkingSetMegabytes,
    double ManagedMegabytes,
    int Handles,
    int Threads,
    double? GpuGigabytes,
    PowerState? Power);

/// <summary>ライブモードを閉じるときのまとめ。</summary>
public sealed record SessionSummary(
    TimeSpan Duration,
    RenderPeriod Render,
    TrackingPeriod? Tracking,
    double CpuMean,
    double CpuMax,
    MemoryTrend Memory,
    double? GpuPeakGigabytes,
    PowerState? FirstPower,
    PowerState? LastPower);

/// <summary>速さと重さの計算（ライブモードの速さのログ）。</summary>
public static class PerformanceMath
{
    /// <summary>
    /// この間に使った CPU の時間を、PC の CPU 全体（論理 CPU の数 × 時間）のうちの割合（%）にする（タスク マネージャーと同じ見方）。
    /// </summary>
    public static double CpuPercent(TimeSpan cpu, TimeSpan wall, int processors) =>
        wall <= TimeSpan.Zero || processors < 1 ? 0 : Math.Clamp(cpu.TotalMilliseconds / (wall.TotalMilliseconds * processors) * 100, 0, 100);
}

/// <summary>
/// ライブモードの速さのログの文（設計判断記録 77 章）。1 分ごとに「[速さ]」（描画・トラッキング・仮想カメラ）と「[重さ]」（CPU・メモリ・GPU・電源）の 2 行、
/// トラッキングを止めたときと、閉じるときに「[まとめ]」を書く。
/// </summary>
public static class PerformanceText
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>「1 分 30 秒」「2 時間 5 分」「45 秒」の形。</summary>
    public static string Duration(TimeSpan duration)
    {
        // 先に秒へ丸める（1 分に少し足りない 59.999 秒が「60 秒」にならないように。ノートパソコンのログで見つけた）
        duration = TimeSpan.FromSeconds(Math.Max(0, Math.Round(duration.TotalSeconds)));
        if (duration.TotalHours >= 1)
        {
            return duration.Minutes == 0 ? $"{(int)duration.TotalHours} 時間" : $"{(int)duration.TotalHours} 時間 {duration.Minutes} 分";
        }
        if (duration.TotalMinutes >= 1)
        {
            return duration.Seconds == 0 ? $"{(int)duration.TotalMinutes} 分" : $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
        }
        return $"{(int)duration.TotalSeconds} 秒";
    }

    /// <summary>CPU の作り（ARM64 の PC で x64 の版をまねて動かしているときは、そう書く）。</summary>
    public static string Machine(Architecture process, Architecture? machine)
    {
        if (machine is not { } native || native == process)
        {
            return Name(process);
        }
        return $"{Name(native)}（{Name(process)} の版を、まねて動かしている）";

        static string Name(Architecture architecture) => architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
            System.Runtime.InteropServices.Architecture.Arm => "ARM",
            _ => architecture.ToString(),
        };
    }

    public static string Render(RenderPeriod p)
    {
        var parts = new List<string>(4);
        if (p.Frame.Count > 0)
        {
            parts.Add(string.Format(C, "1 回 平均 {0:F1} ms・95% {1:F1} ms", p.Frame.Mean, p.Frame.P95));
        }
        if (p.Gpu.Count > 0)
        {
            parts.Add(string.Format(C, "GPU {0:F1} ms", p.Gpu.Mean));
        }
        parts.Add(p.Hitches > 0
            ? string.Format(C, "つかえ {0} 回（いちばん長い間 {1:F0} ms）", p.Hitches, p.LongestStep)
            : "つかえ 0 回");
        if (p.Pauses > 0)
        {
            parts.Add(string.Format(C, "止まった {0} 回", p.Pauses));
        }
        return string.Format(C, "描画 {0:F1} 回/秒（{1}）", p.FramesPerSecond, string.Join("・", parts));
    }

    /// <summary>トラッキングの速さ。<paramref name="within"/> を渡すと、その一部の間だけ動かしていたときに、動かしていた時間を書く。</summary>
    public static string Tracking(TrackingPeriod p, TimeSpan? within = null)
    {
        var parts = new List<string>(6);
        if (within is null)
        {
            parts.Add(Duration(p.Duration));
        }
        else if (p.Duration < within.Value * 0.95)
        {
            parts.Add($"動かしていた {Duration(p.Duration)}");
        }
        parts.AddRange(TrackingDetails(p));
        return string.Format(C, "{0} {1:F1} 回/秒（{2}）", TrackingName(p), p.FramesPerSecond, string.Join("・", parts));
    }

    private static string TrackingName(TrackingPeriod p) => p.Phone ? "トラッキング（スマホ）" : "トラッキング";

    private static List<string> TrackingDetails(TrackingPeriod p)
    {
        var parts = new List<string>(4);
        if (!p.Phone && p.Process.Count > 0)
        {
            parts.Add(string.Format(C, "処理 平均 {0:F1} ms・95% {1:F1} ms・最大 {2:F1} ms", p.Process.Mean, p.Process.P95, p.Process.Max));
            parts.Add(string.Format(C, "映像待ち 平均 {0:F1} ms", p.Wait.Mean));
        }
        parts.Add(string.Format(C, "顔あり {0:F0}%", p.FoundRatio * 100));
        if (!p.Phone)
        {
            parts.Add(string.Format(C, "探し直し {0} 回", p.Detected));
        }
        return parts;
    }

    public static string VirtualCamera(VirtualCameraPeriod p) =>
        string.Format(C, "仮想カメラ {0:F1} 回/秒（{1}）", p.FramesPerSecond, p.InUse ? "今、ほかのアプリが使っている" : "今は使われていない");

    /// <summary>1 分ごとの「[速さ]」の行。</summary>
    public static string Speed(TimeSpan wall, RenderPeriod render, TrackingPeriod? tracking, VirtualCameraPeriod? camera)
    {
        var parts = new List<string>(3) { Render(render) };
        parts.Add(tracking is { IsEmpty: false } t ? Tracking(t, wall) : "トラッキング 止めている");
        if (camera is { } c)
        {
            parts.Add(VirtualCamera(c));
        }
        return $"[速さ] {Duration(wall)}: {string.Join("／", parts)}";
    }

    /// <summary>1 分ごとの「[重さ]」の行。</summary>
    public static string Load(LoadSample s)
    {
        var parts = new List<string>(4)
        {
            string.Format(C, "CPU {0:F0}%（PC 全体のうち）", s.CpuPercent),
            string.Format(
                C, "メモリ {0:F0} MB（実際に載っている分 {1:F0} MB・.NET {2:F0} MB・ハンドル {3}・スレッド {4}）",
                s.PrivateMegabytes, s.WorkingSetMegabytes, s.ManagedMegabytes, s.Handles, s.Threads),
        };
        if (s.GpuGigabytes is double gpu)
        {
            parts.Add(string.Format(C, "GPU のメモリ {0:F2} GB", gpu));
        }
        if (s.Power is { } power)
        {
            parts.Add($"電源 {power.Describe()}");
        }
        return $"[重さ] {string.Join("・", parts)}";
    }

    /// <summary>閉じるときの「[まとめ]」の行。</summary>
    public static string Summary(SessionSummary s)
    {
        var parts = new List<string>(6)
        {
            string.Format(
                C, "描画 平均 {0:F1} 回/秒（つかえ {1} 回{2}）", s.Render.FramesPerSecond, s.Render.Hitches,
                s.Render.Pauses > 0 ? string.Format(C, "・止まった {0} 回", s.Render.Pauses) : string.Empty),
        };
        if (s.Tracking is { IsEmpty: false } tracking)
        {
            parts.Add(string.Format(
                C, "{0} 計 {1}（{2:F1} 回/秒・{3}）", TrackingName(tracking), Duration(tracking.Duration), tracking.FramesPerSecond,
                string.Join("・", TrackingDetails(tracking))));
        }
        parts.Add(string.Format(C, "CPU 平均 {0:F0}%・最大 {1:F0}%", s.CpuMean, s.CpuMax));
        MemoryTrend memory = s.Memory;
        if (memory.First is double first)
        {
            string growth = memory.GrowthPerHour is double perHour && memory.GrowthFrom is { } from
                ? string.Format(C, "・増え方 {0:+0;-0;0} MB/時（{1} 分目から）", perHour, (int)from.TotalMinutes)
                : $"・増え方は、{Duration(MemoryTrend.TimeNeeded)}ほど動かすと分かる";
            parts.Add(string.Format(
                C, "メモリ 始め {0:F0} MB・最小 {1:F0} MB・最大 {2:F0} MB・終わり {3:F0} MB{4}", first, memory.Lowest, memory.Peak, memory.Last, growth));
        }
        if (s.GpuPeakGigabytes is double gpu)
        {
            parts.Add(string.Format(C, "GPU のメモリ 最大 {0:F2} GB", gpu));
        }
        if (s.FirstPower is { } start && s.LastPower is { } end)
        {
            parts.Add(start.Describe() == end.Describe() ? $"電源 {end.Describe()}" : $"電源 {start.Describe()} → {end.Describe()}");
        }
        return $"[まとめ] ライブモード {Duration(s.Duration)}: {string.Join("／", parts)}";
    }
}
