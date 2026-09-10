namespace rt4k_pi.Slices;

using System.Runtime.InteropServices;
using rt4k_pi.Filesystem;

public class AppState
{
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public Serial? Serial { get; set; }
    public Ser2net? Ser2net { get; set; }
    public RT4K? RT4K { get; set; }
    public FirmwareCatalog? FirmwareCatalog { get; set; }
    public FirmwareRelease? AvailableFirmwareUpdate => Serial?.IsConnected == true &&
        RT4K?.Power == rt4k_pi.RT4K.PowerState.On && RT4K.Model is "RT4K Pro" or "RT4K CE"
        ? FirmwareCatalog?.FindUpdate(RT4K.Firmware, Settings.IncludeExperimentalFirmware) : null;
    public FuseDaemon? FuseDaemon { get; set; }
    public required Logger Logger { get; set; }
    public required StatusDaemon StatusDaemon { get; set; }
    public required SettingsDaemon Settings { get; set; }
    public required Installer Installer { get; set; }
}