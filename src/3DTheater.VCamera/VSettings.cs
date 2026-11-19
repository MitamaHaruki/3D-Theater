using System.IO;
using System.Text;
using System.Text.Json;

namespace Theater3D.VCamera;

/// <summary>3D Theater VCamera の設定（PC ごと。%APPDATA%\3DTheater\VCamera\settings.json）。</summary>
public sealed class VSettings
{
    /// <summary>使うカメラの名前（無ければ初めのカメラ）。</summary>
    public string? Camera { get; set; }

    /// <summary>送り先（3D Theater の PC の IP か名前）。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>送り先のポート（3D Theater の受け取りの初めのポート）。</summary>
    public int Port { get; set; } = Theater3D.Tracking.Vmc.VmcProtocol.MarionettePort;

    /// <summary>起動したら、すぐ送り始める。</summary>
    public bool StartOnLaunch { get; set; }

    /// <summary>手と指も読んで送る（顔だけより重くなる。設計判断記録 99 章）。</summary>
    public bool Hands { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "3DTheater", "VCamera", "settings.json");

    public static VSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<VSettings>(File.ReadAllText(FilePath, Encoding.UTF8), Options) ?? new VSettings() : new VSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            VLog.Warning("設定を読めなかったため、初めの設定で起動します", ex);
            return new VSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VLog.Warning("設定を保存できませんでした", ex);
        }
    }
}
