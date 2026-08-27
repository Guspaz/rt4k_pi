namespace rt4k_pi;

public class RT4K
{
    public PowerState Power { get; private set; } = PowerState.Unknown;

    // Fields from the most recent successful "status" poll (see PROTOCOL.md, "status")
    public IReadOnlyDictionary<string, string> Status { get; private set; } = new Dictionary<string, string>();

    /// <summary>
    /// Bumped on every completed poll, so a client can wait for fresh data rather than polling
    /// on its own schedule and drifting out of step with the device.
    /// </summary>
    public long Revision { get; private set; }

    // Completed and replaced on every poll, so listeners can await the next one instead of
    // polling. RunContinuationsAsynchronously keeps waiters off the poll loop's thread.
    private volatile TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once a poll newer than <paramref name="seen"/> has landed. Returns at once
    /// if that has already happened.
    /// </summary>
    public async Task WaitForChangeAsync(long seen, CancellationToken token)
    {
        while (Revision == seen && !token.IsCancellationRequested)
        {
            // Captured before re-checking Revision, so a change in between can't be missed
            Task next = changed.Task;

            if (Revision != seen)
            {
                return;
            }

            await next.WaitAsync(token);
        }
    }

    /// <summary>
    /// Publishes the newly polled state and wakes anyone waiting on it. Called on every poll
    /// rather than only on a detected change, since uptime moves each time anyway.
    /// </summary>
    private void Publish()
    {
        Revision++;

        // Release anyone waiting on the old revision and arm the next wait
        TaskCompletionSource previous = changed;
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>Firmware version, e.g. "1.75.0".</summary>
    public string? Firmware => Field("fw");

    /// <summary>Firmware build tag, e.g. "f0807m".</summary>
    public string? BuildTag => Field("tag");

    /// <summary>Device model name, e.g. "RT4K Pro".</summary>
    public string? Model => ModelName(Number("model"));

    /// <summary>
    /// What to call the attached scaler in the UI and the logs. Falls back to the last model
    /// seen (or a generic name on a first run) so it reads sensibly before the device answers.
    /// </summary>
    public static string DisplayName => Program.Settings.ModelName;

    /// <summary>Name of the selected video input, e.g. "HD15 RGBHV".</summary>
    public string? Input => InputName(Number("input_source"));

    /// <summary>Video decoder handling the current input, e.g. "TVP7002".</summary>
    public string? InputChip => InputChipName(Number("input_ic"));

    /// <summary>Selected HDMI output mode, e.g. "1080p60 (1920x1080)".</summary>
    public string? Output => OutputName(Number("output_res"));

    /// <summary>Loaded profile name, or null when none is loaded.</summary>
    public string? Profile
    {
        get
        {
            string? profile = Field("profile");

            // The device reports the literal string "none" rather than omitting the field
            return string.IsNullOrEmpty(profile) || profile == "none" ? null : profile;
        }
    }

    /// <summary>True when the device reports an SD card is present.</summary>
    public bool? SdCardPresent => Number("sd") is int sd ? sd == 1 : null;

    /// <summary>How long the RT4K has been awake, or null if it hasn't reported yet.</summary>
    public TimeSpan? Uptime => Number("uptime_s") is int seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Negotiated baud rate of the serial link the poll came in on.</summary>
    public int? Baud => Number("baud");

    /// <summary>
    /// Total of every dropped-data counter the device reports. Non-zero means the link is
    /// losing bytes, so it's worth surfacing even though the individual counters aren't.
    /// </summary>
    public int? SerialErrors
    {
        get
        {
            string[] counters = ["oerr", "ring_drop", "queue_drop", "linedrop", "u5oerr"];

            // All five appear on the same line, so either the poll saw it or it saw none of them
            return counters.Any(counter => Number(counter) != null)
                ? counters.Sum(counter => Number(counter) ?? 0)
                : null;
        }
    }

    private string? Field(string key) => Status.TryGetValue(key, out string? value) ? value : null;

    private int? Number(string key) => int.TryParse(Field(key), out int value) ? value : null;

    // PROTOCOL.md, "Model map". Underscores are cosmetic here, so they're spaced out.
    private static string? ModelName(int? model) => model switch
    {
        0 => "RT4K Pro",
        1 => "RT4K CE",
        2 => "RT6X Pro",
        3 => "RT6X CE",
        null => null,
        _ => $"Unknown ({model})"
    };

    // PROTOCOL.md, "Input enum -> name map". Headers and spacers can't be selected, so they
    // should never show up here, but they're mapped rather than silently shown as a bare number.
    private static string? InputName(int? input) => input switch
    {
        0 => "HDMI",
        3 => "Front CVBS",
        4 => "Front Y/C",
        7 => "RCA YPbPr",
        8 => "RCA RGsB",
        9 => "RCA CVBS on G",
        12 => "SCART RGBS",
        13 => "SCART RGsB",
        14 => "SCART YPbPr",
        15 => "SCART CVBS",
        16 => "SCART CVBS on G",
        17 => "SCART Y/C",
        20 => "HD15 RGBHV",
        21 => "HD15 RGBS",
        22 => "HD15 RGsB",
        23 => "HD15 YPbPr",
        24 => "HD15 CVBS on Hs",
        25 => "HD15 CVBS on G",
        26 => "HD15 Y/C on G/R",
        27 => "Enhanced Y/C",
        null => null,
        _ => $"Unknown ({input})"
    };

    // PROTOCOL.md, "input_ic": which decoder chip is handling the selected source
    private static string? InputChipName(int? chip) => chip switch
    {
        0 => "None",
        1 => "TVP7002",
        2 => "TW9912",
        3 => "ADV7611",
        null => null,
        _ => $"Unknown ({chip})"
    };

    // PROTOCOL.md, "Output mode map". 16-68 are invalid; 69-74 are custom modeline slots
    // whose real dimensions depend on the loaded file, so they're reported by slot number.
    private static string? OutputName(int? output) => output switch
    {
        0 => "2160p60 (3840x2160)",
        1 => "2160p50 (3840x2160)",
        2 => "1080p60 (1920x1080)",
        3 => "1080p50 (1920x1080)",
        4 => "1440p60 (2560x1440)",
        5 => "1440p50 (2560x1440)",
        6 => "1080p100 (1920x1080)",
        7 => "1440p100 (2560x1440)",
        8 => "1080p120 (1920x1080)",
        9 => "1440p120 (2560x1440)",
        10 => "1080p144 (1920x1080)",
        11 => "1440p144 (2560x1440)",
        12 => "720p (1280x720)",
        13 => "480p (720x480)",
        14 => "240p (1440x240)",
        15 => "240p120 (1440x240)",
        >= 69 and <= 74 => $"Custom slot {output - 68}",
        null => null,
        _ => $"Unknown ({output})"
    };

    /// <summary>Server-side mirror of the device's on-screen display.</summary>
    public OsdMirror Osd { get; }

    private readonly Serial serial;
    private readonly CancellationTokenSource cts = new();

    // How long the RT4K gets to answer a "status" poll before we call it asleep. The device
    // services one command per superloop pass, so this is generous.
    private const int StatusTimeoutMs = 500;

    // Poll harder while we think it's asleep so a power on is noticed quickly
    private const int PollIntervalOnMs = 5000;
    private const int PollIntervalOffMs = 1000;

    // Last status reply, ignoring the fields that change every poll, so we only log changes
    private string? lastStatus;

    public RT4K(Serial serial)
    {
        this.serial = serial;
        Osd = new OsdMirror(serial, this);

        // While the RT4K is in standby its dispatcher doesn't run: every command except
        // "pwr on" is silently discarded. That makes a "status" poll a reliable power probe.
        Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await RefreshPowerAsync();
                await Task.Delay(Power == PowerState.On ? PollIntervalOnMs : PollIntervalOffMs, cts.Token);
            }
        }, cts.Token);

        // Captures on demand: idles until a page is watching, then follows the device's own
        // remote echo so the mirror updates as soon as a keypress lands.
        Task.Run(() => Osd.RunAsync(cts.Token), cts.Token);
    }

    ~RT4K()
    {
        cts.Cancel();
    }

    /// <summary>
    /// Polls the RT4K with "status". A reply means it's awake, silence means it's in standby.
    /// </summary>
    public async Task<PowerState> RefreshPowerAsync()
    {
        if (!serial.IsConnected)
        {
            Power = PowerState.Unknown;
            Publish();

            return Power;
        }

        try
        {
            // Line 4 (the error counters) is the last line every build emits, but the profile
            // line follows it on newer builds, so just collect whatever arrives in the window.
            var lines = await serial.SendCommandAsync("status", line => line.StartsWith("status oerr="), StatusTimeoutMs, echoIf: replies =>
            {
                string fingerprint = StatusFingerprint(replies);

                if (fingerprint == lastStatus)
                {
                    return false;
                }

                lastStatus = fingerprint;
                return true;
            });
            var status = lines.Where(line => line.StartsWith("status ")).ToList();

            if (status.Count == 0)
            {
                if (Power != PowerState.Off)
                {
                    Console.WriteLine($"{DisplayName} is not responding, assuming it's in standby");
                }

                Power = PowerState.Off;
                Publish();

                return Power;
            }

            var fields = new Dictionary<string, string>();

            foreach (string line in status)
            {
                // "status profile=<name>" is always last and its value is the whole line
                // remainder: profile names may contain spaces, so splitting on whitespace
                // would truncate them at the first word.
                const string profilePrefix = "status profile=";

                if (line.StartsWith(profilePrefix))
                {
                    fields["profile"] = line[profilePrefix.Length..].Trim();
                    continue;
                }

                foreach (var field in Serial.ParseFields(line))
                {
                    fields[field.Key] = field.Value;
                }
            }

            Status = fields;

            // Remembered across restarts so the UI isn't stuck on the generic name until the
            // first poll of the next run lands. Unknown ids are skipped: caching "Unknown (5)"
            // would be worse than the generic fallback. The equality check keeps a poll that saw
            // the same model out of the settings log; the save itself is already change-gated.
            if (Model is string model && !model.StartsWith("Unknown") && model != Program.Settings.ModelName)
            {
                Program.Settings.ModelName = model;
            }

            if (Power != PowerState.On)
            {
                Console.WriteLine($"{DisplayName} is on (fw {fields.GetValueOrDefault("fw", "unknown")})");
            }

            Power = PowerState.On;
            Publish();

            return Power;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error polling {DisplayName} status: {ex.Message}");

            Power = PowerState.Unknown;
            Publish();

            return Power;
        }
    }

    public PowerState RefreshPower() => RefreshPowerAsync().GetAwaiter().GetResult();

    // Boils a status reply down to the parts that don't change on their own, so a poll that
    // reports the same state twice in a row can be kept out of the log. The uptime counter and
    // the superloop timing line move every single poll, so both are dropped.
    private static string StatusFingerprint(List<string> lines)
        => string.Join('\n', lines
            .Where(line => !line.StartsWith("status loop_"))
            .Select(line => string.Join(' ', line.Split(' ').Where(token => !token.StartsWith("uptime_s=")))));

    public enum PowerState
    {
        On,
        Off,
        Unknown
    }

    public enum Remote
    {
        Power,
        Menu,
        Up,
        Down,
        Left,
        Right,
        OK,
        Back,
        Diagnostic,
        Status,
        Input,
        Output,
        Scaler,
        SFX,
        ADC,
        Profile,
        Profile1,
        Profile2,
        Profile3,
        Profile4,
        Profile5,
        Profile6,
        Profile7,
        Profile8,
        Profile9,
        Profile10,
        Profile11,
        Profile12,
        Gain,
        Phase,
        Pause,
        Safe,
        Genlock,
        Buffer,
        Resolution4K,
        Resolution1080p,
        Resolution1440p,
        Resolution480p,
        ResolutionUser1,
        ResolutionUser2,
        ResolutionUser3,
        ResolutionUser4,
        Aux1,
        Aux2,
        Aux3,
        Aux4,
        Aux5,
        Aux6,
        Aux7,
        Aux8,
    }

    private static readonly Dictionary<Remote, string> remoteLookup = new()
    {
        { Remote.Power, "pwr"},
        { Remote.Menu, "menu"},
        { Remote.Up, "up"},
        { Remote.Down, "down"},
        { Remote.Left, "left"},
        { Remote.Right, "right"},
        { Remote.OK, "ok"},
        { Remote.Back, "back"},
        { Remote.Diagnostic, "diag"},
        { Remote.Status, "stat"},
        { Remote.Input, "input"},
        { Remote.Output, "output"},
        { Remote.Scaler, "scaler"},
        { Remote.SFX, "sfx"},
        { Remote.ADC, "adc"},
        { Remote.Profile, "prof"},
        { Remote.Profile1, "prof1"},
        { Remote.Profile2, "prof2"},
        { Remote.Profile3, "prof3"},
        { Remote.Profile4, "prof4"},
        { Remote.Profile5, "prof5"},
        { Remote.Profile6, "prof6"},
        { Remote.Profile7, "prof7"},
        { Remote.Profile8, "prof8"},
        { Remote.Profile9, "prof9"},
        { Remote.Profile10, "prof10"},
        { Remote.Profile11, "prof11"},
        { Remote.Profile12, "prof12"},
        { Remote.Gain, "gain"},
        { Remote.Phase, "phase"},
        { Remote.Pause, "pause"},
        { Remote.Safe, "safe"},
        { Remote.Genlock, "genlock"},
        { Remote.Buffer, "buffer"},
        { Remote.Resolution4K, "res4k"},
        { Remote.Resolution1080p, "res1080p"},
        { Remote.Resolution1440p, "res1440p"},
        { Remote.Resolution480p, "res480p"},
        { Remote.ResolutionUser1, "res1"},
        { Remote.ResolutionUser2, "res2"},
        { Remote.ResolutionUser3, "res3"},
        { Remote.ResolutionUser4, "res4"},
        { Remote.Aux1, "aux1"},
        { Remote.Aux2, "aux2"},
        { Remote.Aux3, "aux3"},
        { Remote.Aux4, "aux4"},
        { Remote.Aux5, "aux5"},
        { Remote.Aux6, "aux6"},
        { Remote.Aux7, "aux7"},
        { Remote.Aux8, "aux8"}
    };

    public void SendRemoteString(string remoteString)
        => SendRemoteStringAsync(remoteString).GetAwaiter().GetResult();

    public async Task SendRemoteStringAsync(string remoteString)
    {
        if (!Enum.TryParse(remoteString, true, out Remote remote))
        {
            Console.WriteLine($"Unrecognized remote string: {remoteString}");
            return;
        }

        if (remote == Remote.Power)
        {
            await PowerToggleAsync();
            return;
        }

        await SendRemoteAsync(remote);
    }

    public void SendRemote(Remote remote) => SendRemoteAsync(remote).GetAwaiter().GetResult();

    /// <summary>
    /// Injects a remote key. This goes through the command plane rather than a raw write so it
    /// can't land in the middle of an OSD/file binary session and get swallowed.
    /// </summary>
    public async Task SendRemoteAsync(Remote remote)
    {
        try
        {
            // The device echoes "[COM] Serial Remote: <key>" as soon as it parses the command.
            // Matching on it lets the call return in milliseconds; without a terminal predicate
            // the command plane waits out the whole timeout, which delays the post-keypress OSD
            // refresh badly enough that the press looks like it did nothing.
            await serial.SendCommandAsync(
                "remote " + remoteLookup[remote],
                line => line.Contains("Serial Remote:") || line.Contains("Serial Remote Bad Command"),
                timeoutMs: 500);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send remote key {remote}: {ex.Message}");
        }
    }

    /// <summary>
    /// Wakes the RT4K. "pwr on" is the only command the standby dispatcher honours.
    /// </summary>
    public async Task PowerOnAsync()
    {
        try
        {
            await serial.SendCommandAsync("pwr on", timeoutMs: 500);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to power on the {DisplayName}: {ex.Message}");
            return;
        }

        // Give the unit time to boot before we believe a status poll
        _ = Task.Delay(8000).ContinueWith(_ => RefreshPowerAsync());
    }

    public void PowerOn() => PowerOnAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Puts the RT4K into standby by injecting the remote's power key.
    /// </summary>
    public async Task PowerOffAsync()
    {
        await SendRemoteAsync(Remote.Power);

        _ = Task.Delay(3000).ContinueWith(_ => RefreshPowerAsync());
    }

    public void PowerOff() => PowerOffAsync().GetAwaiter().GetResult();

    public async Task PowerToggleAsync()
    {
        // Make sure we're acting on a fresh reading rather than a stale poll
        switch (await RefreshPowerAsync())
        {
            case PowerState.On:
                await PowerOffAsync();
                break;
            case PowerState.Off:
            case PowerState.Unknown:
                await PowerOnAsync();
                break;
        }
    }

    public void PowerToggle() => PowerToggleAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Debug helper: writes 1 MB of random data to the SD card, reads it back, verifies it
    /// round-tripped intact, reports throughput, then deletes the file.
    /// </summary>
    public async Task<string> BenchmarkAsync(CancellationToken token = default)
    {
        const string path = "test.dat";
        byte[] sent = new byte[1024 * 1024];
        Random.Shared.NextBytes(sent);

        try
        {
            // Pure transport measurement first: rtl1 echo round-trips a payload through the
            // device without touching the SD card, hashing, or the file commit path. All rounds
            // run inside ONE session so we measure streaming throughput, not session setup.
            Console.WriteLine("Benchmark: measuring raw transport with rtl1 echo...");
            byte[] probe = new byte[Serial.MaxPayload];
            Random.Shared.NextBytes(probe);

            // Warm-up session so first-call costs aren't counted
            await serial.EchoManyAsync(probe, 4, token);

            const int echoRounds = 256;
            var (echoBack, echoElapsed) = await serial.EchoManyAsync(probe, echoRounds, token);

            if (!echoBack.AsSpan().SequenceEqual(probe))
            {
                throw new SerialException("rtl1 echo returned different data than it was sent");
            }

            double echoSeconds = echoElapsed.TotalSeconds;

            // Each round trip moves the payload twice (up and back)
            double echoRate = echoRounds * probe.Length * 2 / 1024.0 / echoSeconds;
            double echoTripMs = echoSeconds * 1000 / echoRounds;
            Console.WriteLine($"Benchmark: echo {echoRate:F1} KB/s both ways ({echoTripMs:F2} ms per {probe.Length} byte round trip)");

            Console.WriteLine($"Benchmark: writing {sent.Length / 1024} KB to /{path}...");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await serial.PutFileAsync(path, sent, token: token);
            double writeSeconds = timer.Elapsed.TotalSeconds;

            Console.WriteLine("Benchmark: reading it back...");
            timer.Restart();
            byte[] received = await serial.GetFileAsync(path, token: token);
            double readSeconds = timer.Elapsed.TotalSeconds;

            string verify = received.AsSpan().SequenceEqual(sent)
                ? "data verified OK"
                : $"DATA MISMATCH (sent {sent.Length} bytes, got {received.Length})";

            string result = $"{verify}, echo {echoRate:F1} KB/s, write {sent.Length / 1024 / writeSeconds:F1} KB/s, read {received.Length / 1024 / readSeconds:F1} KB/s";
            Console.WriteLine($"Benchmark: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Benchmark failed: {ex.Message}");
            return $"Failed: {ex.Message}";
        }
        finally
        {
            await serial.SendCommandAsync($"rm {path}", line => line.StartsWith("rm "), token: token);
        }
    }
}
