using System.Runtime.InteropServices;
using Theater3D.Editor;

namespace Theater3D.App.Logging;

/// <summary>PC の様子（CPU の作り・電源）を Windows から読む（ログに書くため）。</summary>
internal static partial class SystemInfo
{
    private const byte NoBattery = 128;
    private const byte UnknownStatus = 255;

    /// <summary>この PC の CPU の作り（ARM64 の PC で、x64 の版をまねて動かしているかが分かる）。読めなければ null。</summary>
    public static Architecture? MachineArchitecture()
    {
        try
        {
            if (IsWow64Process2(GetCurrentProcess(), out _, out ushort native))
            {
                return native switch
                {
                    0x8664 => Architecture.X64,
                    0xAA64 => Architecture.Arm64,
                    0x014C => Architecture.X86,
                    0x01C4 => Architecture.Arm,
                    _ => null,
                };
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 の古い版（1709 より前）には無い
        }
        return null;
    }

    /// <summary>「ARM64（x64 の版を、まねて動かしている）」などの形。</summary>
    public static string MachineText => PerformanceText.Machine(RuntimeInformation.ProcessArchitecture, MachineArchitecture());

    /// <summary>電源の様子。読めなければ null。</summary>
    public static PowerState? ReadPower()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            return null;
        }
        bool hasBattery = status.BatteryFlag != UnknownStatus && (status.BatteryFlag & NoBattery) == 0;
        bool? onBattery = status.ACLineStatus switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
        int? percent = hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        Guid? mode = null;
        try
        {
            if (PowerGetEffectiveOverlayScheme(out Guid overlay) == 0)
            {
                mode = overlay;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // 電源モードの無い版の Windows
        }
        return new PowerState(hasBattery, onBattery, percent, status.SystemStatusFlag == 1, mode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag; // 1 なら省エネ機能（バッテリー節約機能）がオン
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    // 電源モード（最適な電力効率・バランス・最適なパフォーマンス）。Windows 10 1709 から
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}
