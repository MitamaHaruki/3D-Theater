using System.IO;
using Theater3D.App.Logging;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Images;

namespace Theater3D.App.Rendering;

/// <summary>
/// 環境の画像を、作品の描き方の設定（<see cref="RenderSettings.Environment"/>）に合わせて読み込み、GPU に載せておく。
/// 使うファイルが変わったときだけ読み直す（明るさ・回転を変えても読み直さない）。横が 4096 画素を超える画像は縮めて載せる
/// （GPU の記憶を使いすぎないように。環境光と映り込みには十分な細かさ）。読めなければ、理由を <see cref="Error"/> に入れる。
/// </summary>
public sealed class EnvironmentImage : IDisposable
{
    /// <summary>載せる画像の横の上限（画素）。</summary>
    public const int MaxWidth = 4096;

    private readonly NativeEngine? _engine;
    private readonly RenderSettings _settings;
    private string? _file;

    public EnvironmentImage(NativeEngine? engine, RenderSettings settings)
    {
        _engine = engine;
        _settings = settings;
        settings.Changed += (_, _) => Update();
    }

    /// <summary>載せた画像（使わない・読めないときは null）。</summary>
    public NativeEnvironment? Current { get; private set; }

    /// <summary>読めなかった理由（読めたとき・使わないときは null）。</summary>
    public string? Error { get; private set; }

    /// <summary>読み込んだファイルの、元の大きさ（縮める前）。</summary>
    public (int Width, int Height) SourceSize { get; private set; }

    /// <summary>載せた画像・読めなかった理由が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>設定に合わせて読み込む（使うファイルが変わったときだけ）。</summary>
    public void Update()
    {
        SceneEnvironment environment = _settings.Environment;
        string? wanted = environment.IsActive ? environment.File : null;
        if (string.Equals(wanted, _file, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Release();
        _file = wanted;
        if (wanted is not null && _engine is not null)
        {
            try
            {
                HdrImage source = HdrDecoder.Decode(File.ReadAllBytes(wanted));
                SourceSize = (source.Width, source.Height);
                HdrImage image = source.Downsampled(MaxWidth);
                Current = _engine.CreateEnvironment(image.Width, image.Height, image.Rgb);
                AppLog.Info($"環境の画像を読み込みました: {wanted}（{source.Width} x {source.Height}、GPU には {image.Width} x {image.Height}）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModelFormatException or NativeEngineException
                or OutOfMemoryException)
            {
                Error = ex is FileNotFoundException or DirectoryNotFoundException ? "ファイルが見つかりません" : ex.Message;
                AppLog.Warning($"環境の画像を読み込めませんでした: {wanted}", ex);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>GPU の画像を手放す（エンジンを破棄する前に呼ぶ）。</summary>
    public void Dispose()
    {
        Release();
        _file = null;
    }

    private void Release()
    {
        Current?.Dispose();
        Current = null;
        Error = null;
        SourceSize = default;
    }
}
