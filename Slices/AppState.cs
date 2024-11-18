namespace rt4k_pi.Slices;

using System.Runtime.InteropServices;

public class AppState
{
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public Serial? Serial { get; set; }
    public Ser2net? Ser2net { get; set; }
    public required Logger Logger { get; set; }
    public required StatusDaemon StatusDaemon { get; set; }
    public required SettingsDaemon Settings { get; set; }

    public string? Command { get; set; }
}