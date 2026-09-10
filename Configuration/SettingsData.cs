namespace rt4k_pi;

public sealed record SettingsData
{
    // Setters let source-generated JSON preserve initializers for omitted properties.
    // SettingsDaemon publishes copies and never mutates a published instance.
    public int RemoteScale { get; set; } = 33;
    public int OsdScale { get; set; } = 200;
    public string LatestVersion { get; set; } = Program.VERSION;
    public string ModelName { get; set; } = SettingsDaemon.DefaultModelName;
    public bool VerboseLogging { get; set; }
    public bool WakeOnFileAccess { get; set; } = true;
    public bool EnableSer2net { get; set; } = true;
    public bool IncludeExperimentalFirmware { get; set; } = true;

    public void Validate()
    {
        if (RemoteScale <= 0 || OsdScale <= 0)
        {
            throw new ArgumentException("Display scales must be positive.");
        }

        if (string.IsNullOrWhiteSpace(LatestVersion) || string.IsNullOrWhiteSpace(ModelName))
        {
            throw new ArgumentException("Version and model name must not be empty.");
        }
    }
}
