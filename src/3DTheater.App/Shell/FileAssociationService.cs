using System.Runtime.InteropServices;
using Microsoft.Win32;
using Theater3D.App.Logging;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// .3dtproj・.3dtlive をダブルクリックで開けるようにする登録（ファイルの関連付け）を、今の利用者のレジストリに書く・消す。
/// 書く中身は <see cref="FileAssociations"/>。利用者がメニューで選んだときだけ書く（起動しただけでは書かない）。
/// </summary>
internal static partial class FileAssociationService
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    /// <summary>今動いているアプリの場所（登録する起動の命令に使う）。</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>このアプリ（<paramref name="exePath"/>）で開くように、すべて登録してあるか。</summary>
    public static bool IsRegistered(string? exePath)
    {
        if (exePath is null)
        {
            return false;
        }
        try
        {
            foreach (RegistryValue value in FileAssociations.Entries(exePath))
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{FileAssociations.ClassesKey}\{value.Key}");
                if (key?.GetValue(value.Name ?? string.Empty) is not string text || text != value.Value)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            AppLog.Warning("ファイルの関連付けを読めませんでした", ex);
            return false;
        }
    }

    /// <summary>このアプリで開くように登録する。</summary>
    public static void Register(string exePath)
    {
        foreach (RegistryValue value in FileAssociations.Entries(exePath))
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{FileAssociations.ClassesKey}\{value.Key}", writable: true);
            key.SetValue(value.Name ?? string.Empty, value.Value);
        }
        NotifyShell();
        AppLog.Info($"ファイルの関連付けを登録しました（{string.Join("・", FileAssociations.Types.Select(t => t.Extension))} → {exePath}）");
    }

    /// <summary>登録を消す。拡張子の既定がほかのアプリに変わっていれば、それは残す。</summary>
    public static void Unregister()
    {
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(FileAssociations.ClassesKey, writable: true);
        foreach (var (extension, progId, _) in FileAssociations.Types)
        {
            classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            using (RegistryKey? key = classes.OpenSubKey(extension, writable: true))
            {
                if (key is not null)
                {
                    if (key.GetValue(string.Empty) is string current && current == progId)
                    {
                        key.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }
                    using (RegistryKey? openWith = key.OpenSubKey("OpenWithProgids", writable: true))
                    {
                        openWith?.DeleteValue(progId, throwOnMissingValue: false);
                    }
                    DeleteIfEmpty(key, "OpenWithProgids");
                }
            }
            DeleteIfEmpty(classes, extension);
        }
        NotifyShell();
        AppLog.Info("ファイルの関連付けを外しました");
    }

    private static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        bool empty;
        using (RegistryKey? key = parent.OpenSubKey(name))
        {
            empty = key is not null && key.SubKeyCount == 0 && key.ValueCount == 0;
        }
        if (empty)
        {
            parent.DeleteSubKey(name, throwOnMissingSubKey: false);
        }
    }

    // エクスプローラーに、関連付けが変わったことを知らせる（アイコンと「開く」をすぐに更新させる）
    private static void NotifyShell() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
