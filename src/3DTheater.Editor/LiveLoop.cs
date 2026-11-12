using System.Numerics;

namespace Theater3D.Editor;

/// <summary>ライブモードの時間の決まり（待機モーションのループ）。</summary>
public static class LiveLoop
{
    /// <summary>
    /// 経過時間 <paramref name="seconds"/> のときの、長さ <paramref name="length"/> フレームで繰り返すモーションのフレーム（小数）。
    /// ループ用のモーションは最後のキーが最初のキーと同じ姿勢なので、<paramref name="length"/> フレーム目と 0 フレーム目を同じ時刻にする
    /// （0 から length の手前までを回る）。長さが 0 以下なら 0。
    /// </summary>
    public static float Frame(double seconds, int frameRate, int length)
    {
        if (length <= 0 || frameRate <= 0 || !double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }
        double frame = seconds * frameRate % length;
        return (float)frame;
    }
}

/// <summary>配信用の画面の大きさの候補。</summary>
public static class LiveOutputSizes
{
    public static IReadOnlyList<(int Width, int Height, string Name)> Presets { get; } =
    [
        (1920, 1080, "1920 × 1080（横長）"),
        (1280, 720, "1280 × 720（横長・軽い）"),
        (1080, 1920, "1080 × 1920（縦長）"),
        (1080, 1080, "1080 × 1080（正方形）"),
    ];
}

/// <summary>配信用の画面の背景の色の候補（クロマキーで抜く色など）。</summary>
public static class LiveOutputBackgrounds
{
    public static IReadOnlyList<(Vector3 Color, string Name)> Presets { get; } =
    [
        (new Vector3(0, 1, 0), "グリーンバック（#00FF00）"),
        (new Vector3(0, 0, 1), "ブルーバック（#0000FF）"),
        (new Vector3(1, 0, 1), "マゼンタ（#FF00FF）"),
        (new Vector3(0, 0, 0), "黒"),
        (new Vector3(1, 1, 1), "白"),
    ];
}

/// <summary>
/// ライブモードの配信用の画面の設定（ライブのファイルに保存する）。大きさは配信用の画面の中（描く絵）の画素の数。
/// 背景は、環境の画像を背景に出していなければ、この色で塗る（OBS などのクロマキーで抜く）。
/// </summary>
public sealed class LiveOutputSettings
{
    private int _width = 1920;
    private int _height = 1080;
    private Vector3 _background = new(0, 1, 0);

    public event EventHandler? Changed;

    public int Width => _width;

    public int Height => _height;

    public Vector3 Background
    {
        get => _background;
        set
        {
            Vector3 clamped = Vector3.Clamp(value, Vector3.Zero, Vector3.One);
            if (_background == clamped)
            {
                return;
            }
            _background = clamped;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>大きさを変える（16 から 8192 まで）。</summary>
    public void SetSize(int width, int height)
    {
        width = Math.Clamp(width, 16, 8192);
        height = Math.Clamp(height, 16, 8192);
        if (_width == width && _height == height)
        {
            return;
        }
        _width = width;
        _height = height;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>初めの値（1920 × 1080・グリーンバック）に戻す（新しいライブにしたとき）。</summary>
    public void Reset()
    {
        bool changed = _width != 1920 || _height != 1080 || _background != new Vector3(0, 1, 0);
        _width = 1920;
        _height = 1080;
        _background = new Vector3(0, 1, 0);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
