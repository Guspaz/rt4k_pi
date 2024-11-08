using System.Runtime.InteropServices;

namespace rt4k_pi.Slices
{
    public class AppState
    {
        public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        public Serial? Serial { get; set; }
        public required Logger Logger { get; set; }
    }
}
