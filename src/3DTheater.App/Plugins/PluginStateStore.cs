using System.IO;
using Plugin.Core;
using Theater3D.App.Logging;
using Theater3D.App.Shell;

namespace Theater3D.App.Plugins;

/// <summary>
/// プラグインの有効・無効と、プラグインごとの設定を、利用者の設定（settings.json の Plugins）に持つ（指示書 8 章）。
/// 設定はアプリを閉じるときに保存する。開始の途中の印だけは、落ちても残るよう、すぐにファイルへ書く。
/// </summary>
internal sealed class PluginStateStore(UserSettings settings) : IPluginStateStore
{
    private static string MarkerFile => Path.Combine(UserSettingsStore.Directory, "plugin-starting.txt");

    public bool IsEnabled(string id) => settings.Plugins.TryGetValue(id, out PluginPreferences? p) && p.Enabled;

    public void SetEnabled(string id, bool enabled) => Get(id).Enabled = enabled;

    public string? GetSetting(string id, string key) =>
        settings.Plugins.TryGetValue(id, out PluginPreferences? p) && p.Settings.TryGetValue(key, out string? value) ? value : null;

    public void SetSetting(string id, string key, string? value)
    {
        PluginPreferences preferences = Get(id);
        if (value is null)
        {
            preferences.Settings.Remove(key);
        }
        else
        {
            preferences.Settings[key] = value;
        }
    }

    public string? ReadStartingMarker()
    {
        try
        {
            return File.Exists(MarkerFile) ? File.ReadAllText(MarkerFile).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("プラグインの開始の印を読めませんでした", ex);
            return null;
        }
    }

    public void WriteStartingMarker(string id)
    {
        try
        {
            Directory.CreateDirectory(UserSettingsStore.Directory);
            File.WriteAllText(MarkerFile, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("プラグインの開始の印を書けませんでした", ex);
        }
    }

    public void ClearStartingMarker()
    {
        try
        {
            File.Delete(MarkerFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("プラグインの開始の印を消せませんでした", ex);
        }
    }

    private PluginPreferences Get(string id)
    {
        if (!settings.Plugins.TryGetValue(id, out PluginPreferences? preferences))
        {
            preferences = new PluginPreferences();
            settings.Plugins[id] = preferences;
        }
        return preferences;
    }
}
