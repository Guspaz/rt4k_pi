namespace rt4k_pi;

using System.Text.Json;
using System.Text.Json.Serialization;

public class SettingsDaemon
{
    public const string DefaultModelName = "RetroTINK";
    private readonly Lock gate = new();
    private readonly string fileName = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private SettingsData data = new();
    private bool isLoaded;

    public event Action<bool>? Ser2netChanged;
    private SettingsData Current => Volatile.Read(ref data);

    public int RemoteScale
    {
        get => Current.RemoteScale;
        set => Update(settings => settings with { RemoteScale = value });
    }

    public int OsdScale
    {
        get => Current.OsdScale;
        set => Update(settings => settings with { OsdScale = value });
    }

    public string LatestVersion
    {
        get => Current.LatestVersion;
        set => Update(settings => settings with { LatestVersion = value });
    }

    public string ModelName
    {
        get => Current.ModelName;
        set => Update(settings => settings with { ModelName = value });
    }

    public bool VerboseLogging
    {
        get => Current.VerboseLogging;
        set => Update(settings => settings with { VerboseLogging = value });
    }

    public bool WakeOnFileAccess
    {
        get => Current.WakeOnFileAccess;
        set => Update(settings => settings with { WakeOnFileAccess = value });
    }

    public bool EnableSer2net
    {
        get => Current.EnableSer2net;
        set => Update(settings => settings with { EnableSer2net = value });
    }

    public bool IncludeExperimentalFirmware
    {
        get => Current.IncludeExperimentalFirmware;
        set => Update(settings => settings with { IncludeExperimentalFirmware = value });
    }

    private void Update(Func<SettingsData, SettingsData> change)
    {
        lock (gate)
        {
            SettingsData previous = data;
            SettingsData next = change(previous);
            next.Validate();
            if (next == previous)
            {
                return;
            }

            if (isLoaded)
            {
                Save(next);
            }

            Volatile.Write(ref data, next);
            if (isLoaded && next.EnableSer2net != previous.EnableSer2net)
            {
                try
                {
                    Ser2netChanged?.Invoke(next.EnableSer2net);
                }
                catch
                {
                    Volatile.Write(ref data, previous);
                    Save(previous);
                    throw;
                }
            }
        }
    }

    public void Save()
    {
        lock (gate)
        {
            Save(data);
        }
    }

    private void Save(SettingsData settings)
    {
        string temporary = fileName + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, SourceGenerationContext.Default.SettingsData);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fileName, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Load()
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(fileName))
                {
                    SettingsData loaded = JsonSerializer.Deserialize(File.ReadAllText(fileName), SourceGenerationContext.Default.SettingsData)
                        ?? throw new JsonException("Settings must not be null.");
                    loaded.Validate();
                    Volatile.Write(ref data, loaded);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading {fileName}: {ex.Message}");
            }

            isLoaded = true;
        }
    }

    public IResult UpdateSetting(string name, string value)
    {
        try
        {
            switch (name)
            {
                case nameof(RemoteScale): RemoteScale = int.Parse(value); break;
                case nameof(OsdScale): OsdScale = int.Parse(value); break;
                case nameof(LatestVersion): LatestVersion = value; break;
                case nameof(ModelName): ModelName = value; break;
                case nameof(VerboseLogging): VerboseLogging = bool.Parse(value); break;
                case nameof(WakeOnFileAccess): WakeOnFileAccess = bool.Parse(value); break;
                case nameof(EnableSer2net): EnableSer2net = bool.Parse(value); break;
                case nameof(IncludeExperimentalFirmware): IncludeExperimentalFirmware = bool.Parse(value); break;
                default: return Results.BadRequest("Unknown setting.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update '{name}': {ex.Message}");
            return Results.InternalServerError();
        }

        return Results.Ok();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsData))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}