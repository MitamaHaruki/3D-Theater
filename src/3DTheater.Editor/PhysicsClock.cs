namespace Theater3D.Editor;

public enum PhysicsStepKind
{
    /// <summary>今回は進めない（前の結果のまま）。</summary>
    None,

    /// <summary>剛体を今の姿勢に置き直して、落ち着かせる（フレームを飛ばしたとき・最初）。</summary>
    Reset,

    /// <summary><see cref="PhysicsStep.Seconds"/> 秒進める。</summary>
    Step,
}

/// <summary>物理演算の進め方 1 回分。</summary>
public readonly record struct PhysicsStep(PhysicsStepKind Kind, float Seconds)
{
    public static PhysicsStep None => default;

    public static PhysicsStep Reset => new(PhysicsStepKind.Reset, 0);
}

/// <summary>
/// 物理演算をいつ・どれだけ進めるかを決める（画面の更新ごとに 1 回 <see cref="Next"/> を呼ぶ）。物理は前の結果に続けて計算するので、
/// フレームが続いているときだけ進め、飛んだときは剛体を置き直す。
/// - 再生中・1 フレームずつ送ったとき: 進んだフレームの分だけ進める（4 フレームまで）。それより大きく飛んだら置き直す（ループで先頭へ戻ったときなど）
/// - 止まっているとき: フレームが飛んだら置き直す。姿勢を変えたら（<see cref="NoteEdit"/>）、しばらく実際の時間で動かして落ち着かせる
/// </summary>
public sealed class PhysicsClock
{
    /// <summary>姿勢を変えてから、物理を動かし続ける時間。</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    /// <summary>続けて進めるフレームの数の上限（これより大きく飛んだら置き直す）。</summary>
    public const int MaxStepFrames = 4;

    /// <summary>止まっているときに 1 回で進める時間の上限（秒）。画面の更新が遅れても、物理が大きく飛ばないように。</summary>
    public const float MaxSettleStep = 1f / 30f;

    private int _lastFrame = -1;
    private bool _resetRequested = true;
    private DateTime _settleUntil = DateTime.MinValue;

    /// <summary>次の回で置き直す（物理を入れ直したとき・プロジェクトを開いたときなど）。</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>止まっているときに姿勢を変えた。しばらく物理を動かして落ち着かせる。</summary>
    public void NoteEdit(DateTime now) => _settleUntil = now + SettleTime;

    public bool IsSettling(DateTime now) => now < _settleUntil;

    public PhysicsStep Next(int frame, bool playing, int frameRate, TimeSpan elapsed, DateTime now)
    {
        if (_resetRequested || _lastFrame < 0)
        {
            _resetRequested = false;
            _lastFrame = frame;
            return PhysicsStep.Reset;
        }
        int delta = frame - _lastFrame;
        if (delta != 0)
        {
            _lastFrame = frame;
            // 再生中でも止まっているときでも、すぐ次のフレームへ進んだのなら続けて計算する
            bool continuous = playing ? delta is >= 1 and <= MaxStepFrames : delta == 1;
            return continuous ? new PhysicsStep(PhysicsStepKind.Step, delta / (float)Math.Max(1, frameRate)) : PhysicsStep.Reset;
        }
        if (!playing && now < _settleUntil && elapsed > TimeSpan.Zero)
        {
            return new PhysicsStep(PhysicsStepKind.Step, Math.Min((float)elapsed.TotalSeconds, MaxSettleStep));
        }
        return PhysicsStep.None;
    }
}
