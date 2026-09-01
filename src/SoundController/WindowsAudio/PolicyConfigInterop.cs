using System.Runtime.InteropServices;

namespace SoundController.WindowsAudio;

/// <summary>
/// Minimal COM interop for the undocumented <c>IPolicyConfig</c> interface,
/// the only supported way to change Windows default audio endpoints from a
/// user-mode process. This is the same mechanism used by SoundSwitch,
/// EarTrumpet, and AudioDeviceCmdlets.
///
/// The interface is undocumented; the vtable below has been stable from
/// Windows 10 1803 through Windows 11 24H2 (build 26100). Keep changes to
/// this file isolated: a wrong vtable here can crash the audio subsystem.
/// </summary>
[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] // CLSID_PolicyConfigClient
public class PolicyConfigClient
{
}

/// <summary>
/// Primary interface IID, stable Windows 10 1803 through Windows 11 24H2.
/// Method order matches the native vtable exactly - do not reorder.
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr waveFormat);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr waveFormat);
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr waveFormat, IntPtr waveFormatOld);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriodOut);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int notUsed, IntPtr propertyStore);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int notUsed, IntPtr propertyStore);
    // The operation we actually need: sets the default endpoint for one role.
    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

/// <summary>
/// Fallback IID with an identical vtable, used by AudioDeviceCmdlets. Some
/// Windows builds register only one of the two IIDs, so we query both.
/// </summary>
[ComImport]
[Guid("CA286FC3-91FD-42C3-8E9B-38384CADD54B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigFallback
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr waveFormat);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr waveFormat);
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr waveFormat, IntPtr waveFormatOld);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriodOut);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int notUsed, IntPtr propertyStore);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int notUsed, IntPtr propertyStore);
    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

/// <summary>
/// Thin wrapper that acquires <c>IPolicyConfig</c> and sets default endpoints.
/// Not thread-safe by itself; callers serialize restore operations.
/// </summary>
internal static class PolicyConfigInterop
{
    // ERole values from mmdeviceapi.h. Kept explicit rather than enum to keep
    // this file dependency-free.
    internal const int RoleConsole = 0;
    internal const int RoleMultimedia = 1;
    internal const int RoleCommunications = 2;

    /// <summary>
    /// Sets the default endpoint for one Windows role. Throws on failure so
    /// callers can log, retry, or report. Retrying after a short delay is the
    /// caller's job: endpoint changes are not instantaneous on Windows.
    /// </summary>
    public static void SetDefaultEndpoint(string deviceId, int role)
    {
        try
        {
            var primary = (IPolicyConfig)new PolicyConfigClient();
            ThrowIfFailed(primary.SetDefaultEndpoint(deviceId, role), deviceId, role);
            return;
        }
        catch (InvalidCastException)
        {
            // QueryInterface for the primary IID failed; try the fallback IID.
        }

        var secondary = (IPolicyConfigFallback)new PolicyConfigClient();
        ThrowIfFailed(secondary.SetDefaultEndpoint(deviceId, role), deviceId, role);
    }

    private static void ThrowIfFailed(int hr, string deviceId, int role)
    {
        if (hr < 0)
        {
            throw Marshal.GetExceptionForHR(hr, new IntPtr(-1))
                ?? new InvalidOperationException($"SetDefaultEndpoint failed with HRESULT 0x{hr:X8} for {deviceId} role {role}");
        }
    }
}
