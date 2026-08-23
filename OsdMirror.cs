namespace rt4k_pi;

/// <summary>
/// Server-side mirror of the RT4K's on-screen display: pulls the OSD planes over serial,
/// rasterises them here, and hands the web UI a finished PNG so the browser needs no
/// rendering logic of its own.
/// </summary>
/// <remarks>
/// A single loop owns all capturing, so pulls are inherently single-flighted, and it only runs
/// while a client is actually watching. The glyph ROM is static across builds and is fetched
/// once; the banner changes only on a profile or image change, so it is checked on a slow
/// cadence off the capture path and frames are composed from the last known copy.
/// </remarks>
public class OsdMirror(Serial serial, RT4K rt4k)
{
    // Gap between the primary plane and the secondary plane drawn beside it
    private const int PlaneGap = 16;

    // How long the capture loop waits between passes when nothing has prompted it. A keypress
    // wakes it immediately, so this only bounds how fast changes made at the device itself show up.
    private const int IdlePollMs = 1000;

    // The device services one command per superloop pass, so the OSD isn't repainted the instant
    // the remote echo comes back. Give it a moment before capturing or we mirror the previous frame.
    private const int KeypressSettleMs = 120;

    // The banner only changes when a profile is loaded or the user picks a new image, so it's
    // checked on this much slower cadence instead of once per captured frame.
    private const int BannerCheckMs = 5000;

    // How long a client heartbeat keeps the mirror capturing. Must comfortably exceed the
    // client's heartbeat interval so an in-flight request never lets the lease lapse.
    private const int WatchLeaseMs = 4000;

    // How far a stale frame is dimmed once the OSD is gone, so it reads as "no longer live"
    private const double StaleDim = 0.45;

    private readonly SemaphoreSlim captureLock = new(1, 1);

    // Set when something happened that the OSD is expected to reflect, so the loop stops waiting
    private readonly SemaphoreSlim trigger = new(0, 1);

    // Expiry of the current "someone is watching" lease, renewed by client heartbeats. Ticks
    // rather than DateTime so it can be updated atomically.
    private long watchedUntil = DateTime.MinValue.Ticks;

    private byte[]? font;

    private string? bannerPath;
    private Bitmap? banner;
    private DateTime lastBannerCheck = DateTime.MinValue;

    // Cleared whenever the OSD goes away, so the banner is re-checked when it next appears
    private bool bannerChecked;

    // Whether the current primary plane leaves the banner's rows empty. Screens that write their
    // own text up there must not have the banner painted over it.
    private int blankTopRows;

    private Bitmap? lastFrame;
    private byte[]? lastPng;
    private bool lastWasLive;

    /// <summary>The most recent frame, or null if nothing has been captured yet.</summary>
    public byte[]? CurrentPng => lastPng;

    /// <summary>
    /// Bumped whenever the frame or state actually changes, so a client can tell a genuinely
    /// new frame from a repeat without comparing image bytes.
    /// </summary>
    public long Revision { get; private set; }

    // Completed and replaced on every change, so listeners can await the next frame instead of
    // polling for one. RunContinuationsAsynchronously keeps waiters off the capture loop's thread.
    private volatile TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the frame or state differs from <paramref name="seen"/>. Returns at once
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
    /// Asks the mirror to capture as soon as it can, because something happened that the OSD is
    /// expected to reflect. Cheap and non-blocking; coalesces if a capture is already due.
    /// </summary>
    public void Invalidate()
    {
        try
        {
            trigger.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already flagged; one pass will pick up whatever changed
        }
    }

    /// <summary>
    /// Runs one capture pass per trigger (or per idle tick) for as long as someone is watching.
    /// This is the only caller of <see cref="CaptureAsync"/>, so captures can't overlap and the
    /// rate limiting the old pull path needed is inherent.
    /// </summary>
    public async Task RunAsync(CancellationToken token)
    {
        serial.LineObserved += OnSerialLine;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Nobody is looking, so don't put traffic on a link this slow. Waiting on the
                // trigger rather than sleeping means a page that opens mid-idle starts capturing
                // immediately, and any trigger raised while unwatched is consumed here.
                if (!IsWatched)
                {
                    await trigger.WaitAsync(IdlePollMs, token);
                    continue;
                }

                bool triggered = await trigger.WaitAsync(IdlePollMs, token);

                if (triggered)
                {
                    // The remote echo is printed when the key is parsed, not when the menu acts
                    // on it, so the repaint we want hasn't happened yet.
                    await Task.Delay(KeypressSettleMs, token);
                }

                await captureLock.WaitAsync(token);

                try
                {
                    await CaptureAsync(token);

                    // Deliberately after the capture: the frame the user is waiting on goes out
                    // first, and the banner catches up on its own much slower schedule.
                    await MaintainBannerAsync(token);
                }
                finally
                {
                    captureLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
        finally
        {
            serial.LineObserved -= OnSerialLine;
        }
    }

    /// <summary>
    /// Watches for the device's parse-time remote echo. Triggering off the wire rather than off
    /// the web button means keys injected by anything sharing this link also refresh the mirror.
    /// </summary>
    private void OnSerialLine(string line)
    {
        if (line.Contains("Serial Remote:"))
        {
            Invalidate();
        }
    }

    /// <summary>
    /// Marks the mirror as being watched for the next <see cref="WatchLeaseMs"/>. Clients renew
    /// this while their page is open.
    /// </summary>
    /// <remarks>
    /// A lease rather than a connection count because a browser that navigates away leaves its
    /// SSE socket looking healthy for far longer than we want to keep polling: small writes sit
    /// in the send buffer and succeed, so neither RequestAborted nor a write error arrives
    /// promptly. An expiring lease fails closed instead.
    /// </remarks>
    public void Watch()
    {
        long until = DateTime.UtcNow.AddMilliseconds(WatchLeaseMs).Ticks;

        bool wasIdle = !IsWatched;

        // Never let a late heartbeat shorten a lease granted by another client
        long current = Interlocked.Read(ref watchedUntil);

        while (until > current)
        {
            long previous = Interlocked.CompareExchange(ref watchedUntil, until, current);

            if (previous == current)
            {
                break;
            }

            current = previous;
        }

        // A page that just started watching wants a frame now, not at the next idle tick
        if (wasIdle)
        {
            Invalidate();
        }
    }

    /// <summary>True while at least one client has a live lease.</summary>
    private bool IsWatched => DateTime.UtcNow.Ticks < Interlocked.Read(ref watchedUntil);

    private async Task CaptureAsync(CancellationToken token)
    {
        OsdState previousState = State;
        byte[]? previousPng = lastPng;

        await CaptureCoreAsync(token);

        // Only a real change is worth waking every connected page for
        if (State != previousState || !ReferenceEquals(lastPng, previousPng))
        {
            Revision++;

            // Release anyone waiting on the old revision and arm the next wait
            TaskCompletionSource previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private async Task CaptureCoreAsync(CancellationToken token)
    {
        if (!serial.IsConnected)
        {
            State = OsdState.Disconnected;
            GoStale();
            return;
        }

        if (rt4k.Power != RT4K.PowerState.On)
        {
            State = OsdState.PoweredOff;
            GoStale();
            return;
        }

        try
        {
            font ??= await serial.GetFontAsync(token, quiet: true);

            var (primaryFailed, primary) = await CapturePlaneAsync(aux: false, token);
            var (secondaryFailed, secondary) = await CapturePlaneAsync(aux: true, token);

            if (primaryFailed || secondaryFailed)
            {
                // A plane we couldn't read is not a plane that vanished. Composing now would drop
                // or shift a pane on screen for what is really just a dropped round-trip, so hold
                // the last frame and try again next tick.
                return;
            }

            if (primary == null && secondary == null)
            {
                // Nothing is on screen: keep showing the last frame, dimmed
                State = OsdState.Blank;
                GoStale();
                return;
            }

            // First frame of a fresh OSD: fetch the banner before composing rather than after,
            // or the user watches the banner pop in a moment later. Subsequent frames reuse the
            // cache and let MaintainBannerAsync keep it current off the critical path.
            if (!bannerChecked)
            {
                await RefreshBannerAsync(token);
                bannerChecked = true;
            }

            // Composed from whatever banner we last fetched: it changes only on a profile or
            // image change, so it's refreshed on its own schedule rather than blocking a capture.
            lastFrame = Compose(primary, secondary);
            lastPng = Png.Encode(lastFrame);
            lastWasLive = true;
            State = OsdState.Live;
        }
        catch (Exception ex)
        {
            if (Program.Settings.VerboseLogging)
            {
                Console.WriteLine($"OSD capture failed: {ex.Message}");
            }

            State = OsdState.Error;
            GoStale();
        }
    }

    /// <summary>
    /// Pulls one text plane and rasterises it, or returns Hidden when that plane isn't showing.
    /// </summary>
    /// <remarks>
    /// The device happily hands back the primary grid when the menu is closed, just filled with
    /// blanks, so an empty grid is treated the same as "nothing shown". That also keeps the
    /// banner from lingering on screen after the menu is dismissed. A failed round-trip is
    /// reported separately from a hidden plane: composing a frame from a half-finished capture
    /// would make planes flicker in and out for reasons the user can't see.
    /// </remarks>
    private async Task<(bool Failed, Bitmap? Image)> CapturePlaneAsync(bool aux, CancellationToken token)
    {
        try
        {
            if (aux && !await IsAuxShownAsync(token))
            {
                return (false, null);
            }

            var (text, color, info) = await serial.GetOsdAsync(aux, token, quiet: true);

            int rows = Field(info, "rows", aux ? 4 : 32);
            int stride = Field(info, "stride", OsdRenderer.Stride);

            // The primary plane reports its meaningful width as "width", the aux one as "cols"
            int cols = Field(info, aux ? "cols" : "width", aux ? 32 : 40);

            if (IsBlank(text, color, rows, cols, stride))
            {
                if (!aux)
                {
                    // Nothing on the primary plane means no space is reserved for a banner either,
                    // and leaving the previous frame's count behind would let the banner reappear
                    // over an unrelated screen later.
                    blankTopRows = 0;
                }

                return (false, null);
            }

            if (!aux)
            {
                // "banner=1" is reported even on screens that draw their own text where the
                // banner would go, so the grid itself is the only reliable signal. Measured as
                // a row count here because the banner may not be fetched yet on the first frame.
                blankTopRows = CountBlankTopRows(text, color, rows, cols, stride);
            }

            return (false, OsdRenderer.Render(text, color, font!, rows, cols, stride));
        }
        catch (SerialException ex)
        {
            // "osd: nothing shown" and friends genuinely mean this plane isn't up right now;
            // anything else is a transport hiccup and must not be mistaken for a hidden plane.
            bool hidden = ex.Message.Contains("nothing shown", StringComparison.OrdinalIgnoreCase);

            if (hidden && !aux)
            {
                blankTopRows = 0;
            }

            return (!hidden, null);
        }
    }

    /// <summary>
    /// True when every meaningful cell is a blank glyph drawn on the default background, i.e.
    /// there is nothing for the user to see.
    /// </summary>
    private static bool IsBlank(byte[] text, byte[] color, int rows, int cols, int stride)
    {
        for (int y = 0; y < rows; y++)
        {
            if (!IsRowBlank(text, color, y, cols, stride))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Counts the empty rows at the top of a plane, which is how much vertical space is free
    /// for the banner to occupy.
    /// </summary>
    private static int CountBlankTopRows(byte[] text, byte[] color, int rows, int cols, int stride)
    {
        int blank = 0;

        while (blank < rows && IsRowBlank(text, color, blank, cols, stride))
        {
            blank++;
        }

        return blank;
    }

    /// <summary>True when nothing in this cell row would be visible on screen.</summary>
    private static bool IsRowBlank(byte[] text, byte[] color, int row, int cols, int stride)
    {
        for (int x = 0; x < cols; x++)
        {
            int cell = row * stride + x;

            if (cell >= text.Length || cell >= color.Length)
            {
                continue;
            }

            // Anything other than a space (or NUL) is visible content
            if (text[cell] != 0x20 && text[cell] != 0x00)
            {
                return false;
            }

            // A space still shows if it carries a non-default background
            if (((color[cell] >> 6) & 3) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Cheap text query for the secondary plane. Pulling it while on=0 would mirror stale cells.
    /// </summary>
    private async Task<bool> IsAuxShownAsync(CancellationToken token)
    {
        var lines = await serial.SendCommandAsync("osd2 state", line => line.StartsWith("osd2 "), token: token, echoIf: _ => Program.Settings.VerboseLogging);
        string? state = lines.FirstOrDefault(line => line.StartsWith("osd2 "));

        return state != null && Serial.ParseFields(state).GetValueOrDefault("on") == "1";
    }

    /// <summary>
    /// Keeps the cached banner current without holding up a capture. The banner only matters
    /// while the OSD is actually up, so nothing is fetched otherwise; the first live frame after
    /// a blank spell always re-checks, since a profile may have been loaded while we weren't looking.
    /// </summary>
    private async Task MaintainBannerAsync(CancellationToken token)
    {
        if (State != OsdState.Live)
        {
            // Next time the OSD comes up, check before trusting the cached banner
            bannerChecked = false;
            return;
        }

        if (bannerChecked && (DateTime.UtcNow - lastBannerCheck).TotalMilliseconds < BannerCheckMs)
        {
            return;
        }

        try
        {
            bool bannerChanged = await RefreshBannerAsync(token);
            bannerChecked = true;

            // The frame we just published was composed with the previous banner, so redraw it
            if (bannerChanged)
            {
                await CaptureAsync(token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Program.Settings.VerboseLogging)
            {
                Console.WriteLine($"OSD banner check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Re-downloads the banner image only when the device reports a different path.
    /// </summary>
    /// <returns>True if the banner image changed, so anything showing it needs recomposing.</returns>
    private async Task<bool> RefreshBannerAsync(CancellationToken token)
    {
        lastBannerCheck = DateTime.UtcNow;

        var lines = await serial.SendCommandAsync("banner", line => line.StartsWith("banner="), token: token, echoIf: _ => Program.Settings.VerboseLogging);
        string? line = lines.FirstOrDefault(l => l.StartsWith("banner="));

        if (line == null)
        {
            return false;
        }

        var fields = Serial.ParseFields(line);

        // "path" is directly get-able and already carries its extension; don't touch it
        string? path = fields.GetValueOrDefault("banner") == "1" ? fields.GetValueOrDefault("path") : null;

        if (path == bannerPath)
        {
            return false;
        }

        bannerPath = path;
        banner = null;

        if (path == null)
        {
            // Only worth reporting if there was actually one on screen before
            if (bannerChecked)
            {
                Console.WriteLine("OSD banner cleared");
            }

            return true;
        }

        try
        {
            banner = Bmp.Decode(await serial.GetFileAsync(path, token: token, quiet: true));

            // The transfer itself is silenced above, so report the change ourselves: it's rare
            // (a new profile or a user-picked image) and worth seeing without verbose on.
            Console.WriteLine($"OSD banner changed to {path}");
        }
        catch (SerialException ex)
        {
            Console.WriteLine($"Could not read OSD banner {path}: {ex.Message}");
        }

        return true;
    }

    /// <summary>Composites the planes side by side, with the banner overlaid on the primary.</summary>
    /// <remarks>
    /// Everything is rendered 1:1 here; magnification is a client-side concern so the user can
    /// change it without a re-capture. The banner is the same width as an un-doubled plane and
    /// belongs over the top rows of the primary plane, not floating above the whole composite,
    /// so it's only drawn when that plane is actually present.
    /// </remarks>
    private Bitmap Compose(Bitmap? primary, Bitmap? secondary)
    {
        var (width, height) = Layout(primary, secondary);
        var image = new Bitmap(width, height);

        int x = 0;

        if (primary != null)
        {
            image.Blit(primary, x, 0);

            // The banner sits on top of the primary plane's first rows, transparency and all,
            // but only where the screen hasn't drawn its own text in that space
            if (banner != null && blankTopRows * OsdRenderer.GlyphHeight >= banner.Height)
            {
                image.Blit(banner, x, 0);
            }

            x += primary.Width + PlaneGap;
        }

        if (secondary != null)
        {
            image.Blit(secondary, x, 0);
        }

        return image;
    }

    /// <summary>Measures the combined footprint of the cell planes.</summary>
    private static (int Width, int Height) Layout(Bitmap? primary, Bitmap? secondary)
    {
        int width = 0;
        int height = 0;

        if (primary != null)
        {
            width = primary.Width;
            height = primary.Height;
        }

        if (secondary != null)
        {
            width += (width > 0 ? PlaneGap : 0) + secondary.Width;
            height = Math.Max(height, secondary.Height);
        }

        return (width, height);
    }

    /// <summary>Dims the last live frame once, so a lost OSD stays readable but obviously stale.</summary>
    private void GoStale()
    {
        if (!lastWasLive || lastFrame == null)
        {
            return;
        }

        lastWasLive = false;

        lastFrame.Dim(StaleDim);
        lastPng = Png.Encode(lastFrame);
    }

    /// <summary>What the mirror last saw, so the page can explain itself to the user.</summary>
    public OsdState State { get; private set; } = OsdState.Unknown;

    public enum OsdState
    {
        Unknown,
        Disconnected,
        PoweredOff,
        Blank,
        Live,
        Error
    }

    private static int Field(Dictionary<string, string> info, string key, int fallback)
        => info.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;
}
