namespace rt4k_pi;

public class RT4K
{
    public PowerState Power { get; private set; } = PowerState.Unknown;

    // Fields from the most recent successful "status" poll (see PROTOCOL.md, "status")
    public IReadOnlyDictionary<string, string> Status { get; private set; } = new Dictionary<string, string>();

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
            return Power = PowerState.Unknown;
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
                    Console.WriteLine("RT4K is not responding, assuming it's in standby");
                }

                return Power = PowerState.Off;
            }

            var fields = new Dictionary<string, string>();

            foreach (string line in status)
            {
                foreach (var field in Serial.ParseFields(line))
                {
                    fields[field.Key] = field.Value;
                }
            }

            Status = fields;

            if (Power != PowerState.On)
            {
                Console.WriteLine($"RT4K is on (fw {fields.GetValueOrDefault("fw", "unknown")})");
            }

            return Power = PowerState.On;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error polling RT4K status: {ex.Message}");
            return Power = PowerState.Unknown;
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
    {
        if (Enum.TryParse(remoteString, true, out Remote remote))
        {
            if (remote == Remote.Power)
            {
                PowerToggle();
                return;
            }

            SendRemote(remote);
        }
        else
        {
            Console.WriteLine($"Unrecognized remote string: {remoteString}");
        }
    }

    public void SendRemote(Remote remote)
    {
        serial.WriteLine("remote " + remoteLookup[remote]);
    }

    /// <summary>
    /// Wakes the RT4K. "pwr on" is the only command the standby dispatcher honours.
    /// </summary>
    public void PowerOn()
    {
        serial.WriteLine("pwr on");

        // Give the unit time to boot before we believe a status poll
        Task.Delay(8000).ContinueWith(_ => RefreshPowerAsync());
    }

    /// <summary>
    /// Puts the RT4K into standby by injecting the remote's power key.
    /// </summary>
    public void PowerOff()
    {
        SendRemote(Remote.Power);

        Task.Delay(3000).ContinueWith(_ => RefreshPowerAsync());
    }

    public void PowerToggle()
    {
        // Make sure we're acting on a fresh reading rather than a stale poll
        switch (RefreshPower())
        {
            case PowerState.On:
                PowerOff();
                break;
            case PowerState.Off:
            case PowerState.Unknown:
                PowerOn();
                break;
        }
    }

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
            //await serial.SendCommandAsync($"rm {path}", line => line.StartsWith("rm "), token: token);
        }
    }
}
