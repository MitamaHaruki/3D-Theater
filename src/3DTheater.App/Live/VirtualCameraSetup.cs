using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using Theater3D.App.Logging;
using Theater3D.Media;

namespace Theater3D.App.Live;

/// <summary>カメラの元の登録の状態。</summary>
public enum VirtualCameraRegistration
{
    /// <summary>登録していない。</summary>
    NotRegistered,

    /// <summary>このアプリの DLL と同じものを登録してある。</summary>
    Registered,

    /// <summary>前の版の DLL を登録してある（登録し直す）。</summary>
    Outdated,
}

/// <summary>
/// 仮想カメラの元（COM の部品）の Windows への登録。Windows のカメラの仕組み（Frame Server。Local Service）と、カメラを使うアプリの
/// どちらからも読めるよう、HKLM に登録し、DLL は Program Files の下へ写す（利用者のフォルダーは Local Service から読めないことがある）。
/// どちらも管理者の権限が要るので、このアプリを管理者として起動し直して行う（確認の窓が出る）。起動し直したアプリは窓を出さずに終わる。
/// DLL は版ごと（中身のハッシュ）のフォルダーに置く（前の版を Frame Server が読み込んだままでも、上書きせずに入れ替えられる）。
/// </summary>
public static class VirtualCameraSetup
{
    public const string RegisterSwitch = "--register-virtual-camera";
    public const string UnregisterSwitch = "--unregister-virtual-camera";

    private const int Cancelled = 1223; // ERROR_CANCELLED（確認の窓で「いいえ」）

    private static string ClassKey => $@"SOFTWARE\Classes\CLSID\{VirtualCameraProtocol.Clsid}";

    /// <summary>アプリの隣にあるカメラの元の DLL。</summary>
    public static string SourceDll => Path.Combine(AppContext.BaseDirectory, VirtualCameraProtocol.DllName);

    /// <summary>登録した DLL を置く場所（版ごとのフォルダーの親）。</summary>
    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "3D Theater", "VirtualCamera");

    /// <summary>今の登録の状態（HKLM を読むだけ。権限は要らない）。</summary>
    public static VirtualCameraRegistration Check()
    {
        string? registered = RegisteredPath();
        if (registered is null || !File.Exists(registered))
        {
            return VirtualCameraRegistration.NotRegistered;
        }
        try
        {
            return File.Exists(SourceDll) && Hash(registered) == Hash(SourceDll) ? VirtualCameraRegistration.Registered : VirtualCameraRegistration.Outdated;
        }
        catch (IOException)
        {
            return VirtualCameraRegistration.Outdated;
        }
    }

    /// <summary>登録してある DLL の場所（無ければ null）。</summary>
    public static string? RegisteredPath()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ClassKey + @"\InprocServer32");
        return key?.GetValue(null) as string;
    }

    /// <summary>
    /// このアプリを管理者として起動し直して、登録する（<paramref name="register"/> が false なら外す）。終わるまで待つ。
    /// 確認の窓で「いいえ」を選んだら <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async Task RunElevatedAsync(bool register)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("アプリの場所が分かりません");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = register ? $"{RegisterSwitch} \"{SourceDll}\"" : UnregisterSwitch,
        };
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("管理者として起動できませんでした");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            throw new OperationCanceledException("管理者の確認で「いいえ」が選ばれました", ex);
        }
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{(register ? "登録" : "登録を外すの")}に失敗しました（終了コード {process.ExitCode}。詳しくはログを見てください）");
            }
        }
    }

    /// <summary>
    /// 管理者として起動し直されたときの本体（窓を出さない）。引数が登録・外す指示でなければ false。
    /// </summary>
    public static bool TryRunCommand(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        if (args.Count == 0 || (args[0] != RegisterSwitch && args[0] != UnregisterSwitch))
        {
            return false;
        }
        try
        {
            if (args[0] == RegisterSwitch)
            {
                Register(args.Count > 1 ? args[1] : SourceDll);
            }
            else
            {
                Unregister();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            AppLog.Error(args[0] == RegisterSwitch ? "仮想カメラの元を登録できませんでした" : "仮想カメラの元の登録を外せませんでした", ex);
            exitCode = 1;
        }
        return true;
    }

    // DLL を版ごとのフォルダーへ写し、Local Service に読む権限を付け、HKLM に場所を書く
    private static void Register(string source)
    {
        if (!File.Exists(source))
        {
            throw new IOException($"カメラの元の DLL がありません: {source}");
        }
        string folder = Path.Combine(InstallRoot, Hash(source)[..12]);
        Directory.CreateDirectory(folder);
        var directory = new DirectoryInfo(folder);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        string target = Path.Combine(folder, VirtualCameraProtocol.DllName);
        if (!File.Exists(target) || Hash(target) != Hash(source))
        {
            File.Copy(source, target, overwrite: true);
        }

        using (RegistryKey classKey = Registry.LocalMachine.CreateSubKey(ClassKey, writable: true))
        {
            classKey.SetValue(null, "3D Theater 仮想カメラ");
            using RegistryKey server = classKey.CreateSubKey("InprocServer32", writable: true);
            server.SetValue(null, target);
            server.SetValue("ThreadingModel", "Both");
        }
        AppLog.Info($"仮想カメラの元を登録しました: {target}");
        RemoveOldVersions(keep: folder);
    }

    private static void Unregister()
    {
        Registry.LocalMachine.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);
        AppLog.Info("仮想カメラの元の登録を外しました");
        RemoveOldVersions(keep: null);
    }

    // 前の版のフォルダーを消す（Frame Server が読み込んだままなら消せないので、次の機会に回す）
    private static void RemoveOldVersions(string? keep)
    {
        if (!Directory.Exists(InstallRoot))
        {
            return;
        }
        foreach (string folder in Directory.GetDirectories(InstallRoot))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"前の版のカメラの元を、今は消せませんでした（使われている可能性があります）: {folder}");
            }
        }
        if (keep is null)
        {
            try
            {
                Directory.Delete(InstallRoot);
                Directory.Delete(Path.GetDirectoryName(InstallRoot)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 中身が残っていれば消さない
            }
        }
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
