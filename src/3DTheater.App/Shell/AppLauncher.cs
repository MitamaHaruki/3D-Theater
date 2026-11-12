using System.Diagnostics;
using System.Windows;
using Theater3D.App.Logging;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>もう一方のモードを、別のアプリ（新しい窓）として起動する。</summary>
internal static class AppLauncher
{
    /// <summary><paramref name="mode"/> のモードで新しく起動する。<paramref name="file"/> があれば、それを開く。</summary>
    public static bool Start(AppMode mode, string? file = null)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            AppLog.Warning("アプリの場所が分からないため、新しく起動できませんでした");
            return false;
        }
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add(AppModes.Option(mode));
        if (file is not null)
        {
            info.ArgumentList.Add(file);
        }
        try
        {
            Process.Start(info)?.Dispose();
            AppLog.Info($"{AppModes.DisplayName(mode)}モードを別の窓で起動しました{(file is null ? string.Empty : $"（{file}）")}");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"{AppModes.DisplayName(mode)}モードを起動できませんでした", ex);
            MessageBox.Show($"{AppModes.DisplayName(mode)}モードを起動できませんでした。\n\n{ex.Message}", "3D Theater", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
