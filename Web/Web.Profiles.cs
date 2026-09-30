namespace rt4k_pi;

using Microsoft.AspNetCore.Mvc;
using rt4k_pi.Filesystem;
using System.Text.Json.Serialization;

public record ProfileEntry(string Name, bool IsDirectory, long Size);
public record ProfileListing(string Path, ProfileEntry[] Entries, string? Loaded, bool Truncated);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileListing))]
[JsonSerializable(typeof(string[]))]
public partial class ProfileJsonContext : JsonSerializerContext
{
}

public partial class Program
{
    // Profiles live under /profile on the SD card; "prof load" takes names relative to it
    private const string ProfileRoot = "profile";
    private const int ProfileFolderDepth = 8;

    private static SerialFs? profileFs;

    private static void MapProfiles(WebApplication app)
    {
        app.MapGet("/Profiles/list", (HttpContext context, [FromQuery] string? path) => ProfileAction(context, false, fs =>
        {
            string relative = CleanProfilePath(path);
            string device = ProfileDevicePath(relative);
            var entries = fs.List(device);

            var sorted = entries
                .Where(entry => !entry.Name.StartsWith('.'))
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new ProfileEntry(entry.Name, entry.IsDirectory, entry.Size))
                .ToArray();

            return Results.Json(new ProfileListing(relative, sorted, RT4K?.Profile, sorted.Length >= 512), ProfileJsonContext.Default.ProfileListing);
        }));

        app.MapGet("/Profiles/folders", (HttpContext context, CancellationToken token) => ProfileAction(context, false, fs =>
        {
            var folders = new List<string> { "" };
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue(("", 0));

            while (pending.Count > 0)
            {
                var (current, depth) = pending.Dequeue();
                if (depth >= ProfileFolderDepth)
                {
                    continue;
                }

                foreach (var entry in fs.List(ProfileDevicePath(current)).Where(e => e.IsDirectory && !e.Name.StartsWith('.')).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string child = current.Length == 0 ? entry.Name : $"{current}/{entry.Name}";
                    folders.Add(child);
                    pending.Enqueue((child, depth + 1));
                }
            }

            folders.Sort(StringComparer.OrdinalIgnoreCase);
            return Results.Json(folders.ToArray(), ProfileJsonContext.Default.StringArray);
        }));

        app.MapPost("/Profiles/load", async (HttpContext context, [FromQuery] string path, CancellationToken token) =>
        {
            if (!FirmwareRequestAllowed(context)) { return Results.StatusCode(400); }
            if (ProfileBlocked() is IResult blocked) { return blocked; }

            string relative;
            try { relative = CleanProfilePath(path); }
            catch (ArgumentException ex) { return Results.Text(ex.Message, statusCode: 400); }

            if (relative.Length == 0) { return Results.Text("Choose a profile to load.", statusCode: 400); }

            for (int attempt = 0; attempt < 20; attempt++)
            {
                var lines = await Serial!.SendCommandAsync($"prof load {relative}",
                    line => line.StartsWith("prof load ") || line.StartsWith("prof:"), 15000, token: token);

                if (lines.Contains("prof load ok"))
                {
                    return Results.Ok();
                }

                if (lines.Contains("prof: busy"))
                {
                    await Task.Delay(100, token);
                    continue;
                }

                return Results.Text(lines.Count == 0 ? $"The {rt4k_pi.RT4K.DisplayName} did not respond." : "The profile could not be loaded.", statusCode: 409);
            }

            return Results.Text($"The {rt4k_pi.RT4K.DisplayName} is busy. Try again in a moment.", statusCode: 409);
        });

        app.MapPost("/Profiles/mkdir", (HttpContext context, [FromQuery] string? path, [FromQuery] string name) => ProfileAction(context, true, fs =>
        {
            string folder = CleanProfilePath(path);
            string child = Combine(folder, CleanProfileName(name));
            fs.MakeDirectory(ProfileDevicePath(child));
            return Results.Ok();
        }));

        app.MapPost("/Profiles/rename", (HttpContext context, [FromQuery] string path, [FromQuery] string name) => ProfileAction(context, true, fs =>
        {
            string from = RequireEntry(path);
            int slash = from.LastIndexOf('/');
            string to = Combine(slash < 0 ? "" : from[..slash], CleanProfileName(name));
            if (!string.Equals(from, to, StringComparison.Ordinal))
            {
                MoveProfile(fs, from, to);
            }
            return Results.Ok();
        }));

        app.MapPost("/Profiles/move", (HttpContext context, [FromQuery] string path, [FromQuery] string? to) => ProfileAction(context, true, fs =>
        {
            string from = RequireEntry(path);
            string folder = CleanProfilePath(to);
            string name = from[(from.LastIndexOf('/') + 1)..];

            if (folder.Equals(from, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A folder can't be moved into itself.");
            }

            string destination = Combine(folder, name);
            if (!destination.Equals(from, StringComparison.OrdinalIgnoreCase))
            {
                MoveProfile(fs, from, destination);
            }
            return Results.Ok();
        }));

        app.MapPost("/Profiles/delete", (HttpContext context, [FromQuery] string path) => ProfileAction(context, true, fs =>
        {
            fs.Remove(ProfileDevicePath(RequireEntry(path)));
            return Results.Ok();
        }));
    }

    private static void MoveProfile(SerialFs fs, string from, string to)
    {
        // "mv -f" replaces the destination, so refuse rather than silently overwrite
        if (fs.Stat(ProfileDevicePath(to)) != null)
        {
            throw new ArgumentException("Something with that name already exists there.");
        }

        fs.Rename(ProfileDevicePath(from), ProfileDevicePath(to));
    }

    private static async Task<IResult> ProfileAction(HttpContext context, bool modifies, Func<SerialFs, IResult> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (modifies && !FirmwareRequestAllowed(context)) { return Results.StatusCode(400); }
        if (ProfileBlocked() is IResult blocked) { return blocked; }

        profileFs ??= new SerialFs(Serial!);
        var fs = profileFs;

        try
        {
            return await Task.Run(() => action(fs));
        }
        catch (ArgumentException ex)
        {
            return Results.Text(ex.Message, statusCode: 400);
        }
        catch (SerialFsException ex)
        {
            return Results.Text(ex.Symbol switch
            {
                "NOSUCH" => "That item no longer exists. The list has been refreshed.",
                "EXIST" => "Something with that name already exists there.",
                "NOTEMPTY" => "Only empty folders can be deleted. Move or delete what's inside first.",
                "INVALID_NAME" => "That name isn't allowed on the SD card.",
                "NAME_TOO_LONG" => "That name is too long.",
                "NO_SPACE" => "The SD card is full.",
                "BUSY" => $"The {rt4k_pi.RT4K.DisplayName} is busy. Try again in a moment.",
                "TIMEOUT" => $"The {rt4k_pi.RT4K.DisplayName} did not respond. Make sure it's turned on.",
                _ => $"The {rt4k_pi.RT4K.DisplayName} reported an error ({ex.Symbol})."
            }, statusCode: 409);
        }
        catch (Exception ex) when (ex is SerialException or IOException or TimeoutException)
        {
            return Results.Text(ex.Message, statusCode: 503);
        }
    }

    private static IResult? ProfileBlocked()
    {
        if (Serial == null || !Serial.IsConnected)
        {
            return Results.Text($"The {rt4k_pi.RT4K.DisplayName} is not connected.", statusCode: 503);
        }

        if (Firmware?.Status is { Active: true } or { NeedsRecovery: true })
        {
            return Results.Text("Profiles can't be changed while a firmware update is running.", statusCode: 409);
        }

        return null;
    }

    private static string ProfileDevicePath(string relative) => relative.Length == 0 ? ProfileRoot : $"{ProfileRoot}/{relative}";

    private static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    private static string RequireEntry(string? path)
    {
        string relative = CleanProfilePath(path);
        return relative.Length == 0 ? throw new ArgumentException("Choose a profile or folder.") : relative;
    }

    /// <summary>Normalizes a path relative to /profile, rejecting anything that could escape it.</summary>
    private static string CleanProfilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Select(CleanProfileName));
    }

    private static string CleanProfileName(string? name)
    {
        name = name?.Trim();

        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            throw new ArgumentException("Enter a name.");
        }

        if (name.IndexOfAny(['/', '\\', '|', ':', '*', '?', '"', '<', '>']) >= 0 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Names can't contain / \\ | : * ? \" < >");
        }

        if (name.Length > SerialFs.MaxNameLength)
        {
            throw new ArgumentException("That name is too long.");
        }

        return name;
    }
}
