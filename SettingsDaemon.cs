﻿namespace rt4k_pi;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
    
// For trimming/native aot, a bunch of reflection stuff needs special handling and code gen
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public partial class SettingsDaemon
{
    private bool isLoaded = false;

    private int _RemoteScale = 33;
    public int RemoteScale
    {
        get => _RemoteScale;
        set => SetProperty(ref _RemoteScale, value);
    }

    private string _LatestVersion = Program.VERSION;
    public string LatestVersion
    {
        get => _LatestVersion;
        set => SetProperty(ref _LatestVersion, value);
    }

    private bool _VerboseLogging = false;
    public bool VerboseLogging
    {
        get => _VerboseLogging;
        set => SetProperty(ref _VerboseLogging, value);
    }

    private bool _EnableSer2net = true;
    public bool EnableSer2net
    {
        get => _EnableSer2net;
        set {
            if (!EnableSer2net && value)
            {
                Program.Ser2net?.Start();
            }
            else if (EnableSer2net && !value)
            {
                Program.Ser2net?.Stop();
            }

            SetProperty(ref _EnableSer2net, value);
        }
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        // TODO: Some sort of setting change subscription system?
        // Bypass logic if we're not yet loaded, or were maybe instantiated by the json deserializer
        if (!isLoaded)
        {
            field = value;
            return;
        }

        if (!Equals(field, value))
        {
            Console.WriteLine($"Setting {propertyName} updated from {field} to {value}");
            field = value;
            Save();
        }
        else
        {
            Console.WriteLine($"Setting {propertyName} updated to same value: {field}");
        }
    }

    private const string fileName = "settings.json";

    public void Save()
    {
        // This class is instantiated before the main class has a chance to do this
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        Console.WriteLine($"Saving settings to {fileName}");
        File.WriteAllText(fileName, JsonSerializer.Serialize(this!, SourceGenerationContext.Default.SettingsDaemon));
    }

    public void Load()
    {
        // This class is instantiated before the main class has a chance to do this
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        Console.WriteLine($"Reading settings from {fileName}");

        try
        {
            if (File.Exists(fileName))
            {
                var result = JsonSerializer.Deserialize<SettingsDaemon>(File.ReadAllText(fileName), SourceGenerationContext.Default.SettingsDaemon);

                if (result != null)
                {
                    foreach (var property in GetType().GetProperties())
                    {
                        property.SetValue(this, property.GetValue(result));
                    }

                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading {fileName}: {ex.Message}");
        }

        isLoaded = true;
    }

    public IResult UpdateSetting(string name, string value)
    {
        // Get the property by name
        var property = GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        if (property == null)
        {
            Console.WriteLine($"Error: Property '{name}' does not exist.");
            return Results.BadRequest();
        }

        try
        {
            // Convert the string value to the property type
            var convertedValue = Convert.ChangeType(value, property.PropertyType);

            // Set the property using reflection (triggers the setter)
            property.SetValue(this, convertedValue);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: Failed to update '{name}': {ex.Message}");
            return Results.InternalServerError();
        }

        return Results.Ok();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsDaemon))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}