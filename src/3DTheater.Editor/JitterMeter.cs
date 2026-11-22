using System.Numerics;

namespace Theater3D.Editor;

/// <summary>
/// 回転の揺れを測る（トラッキングの腕の骨・上半身。ログに書いて、ノートパソコンでの確かめで揺れを数で比べる。設計判断記録 100 章）。
/// 揺れ = 1 枚ごとの回転が、前後の枚の真ん中からどれだけずれたか（度）。ゆっくり動かしている分は入らず、行ったり来たりの分だけが入る。
/// 測るもの（腕の骨など）ごとに番号で分けて足し、<see cref="Take"/> でまとめて読む（読むと数え直す）。
/// </summary>
public sealed class JitterMeter
{
    private readonly Dictionary<int, Track> _tracks = [];

    private sealed class Track
    {
        public Quaternion? Before;
        public Quaternion? Last;
        public double Sum;
        public double Max;
        public int Count;
        public int Frames;
        public int Active;
    }

    /// <summary>1 枚分の回転を足す。<paramref name="active"/> でなければ（腕を動かしていない枚）、揺れには入れず、続きも切る。</summary>
    public void Add(int key, Quaternion rotation, bool active)
    {
        if (!_tracks.TryGetValue(key, out Track? t))
        {
            t = new Track();
            _tracks[key] = t;
        }
        t.Frames++;
        if (!active || !float.IsFinite(rotation.W))
        {
            t.Before = t.Last = null;
            return;
        }
        t.Active++;
        if (t.Before is { } a && t.Last is { } b)
        {
            // b が、a と今の回転の真ん中からどれだけずれたか
            Quaternion c = Quaternion.Dot(a, rotation) < 0 ? -rotation : rotation;
            Quaternion middle = Quaternion.Normalize(Quaternion.Slerp(a, c, 0.5f));
            double degrees = 2 * Math.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(middle, Quaternion.Normalize(b))), 0f, 1f)) * (180 / Math.PI);
            t.Sum += degrees * degrees;
            t.Max = Math.Max(t.Max, degrees);
            t.Count++;
        }
        t.Before = t.Last;
        t.Last = Quaternion.Normalize(rotation);
    }

    /// <summary>番号ごとのまとめ（揺れの RMS と最大（度）・動かしていた割合 0〜1）。読むと数え直す。</summary>
    public IReadOnlyDictionary<int, (double Rms, double Max, double ActiveShare)> Take()
    {
        var result = new Dictionary<int, (double, double, double)>();
        foreach (var (key, t) in _tracks)
        {
            result[key] = (t.Count > 0 ? Math.Sqrt(t.Sum / t.Count) : 0, t.Max, t.Frames > 0 ? (double)t.Active / t.Frames : 0);
            t.Sum = t.Max = 0;
            t.Count = t.Frames = t.Active = 0;
        }
        return result;
    }
}
