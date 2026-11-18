namespace rt4k_pi;

/// <summary>What became of a click on the OSD mirror.</summary>
public enum OsdClickResult
{
    // The keys were sent and, for a move, the cursor was seen to land on the item
    Done,

    // Nothing happens when that spot is clicked
    Ignored,

    // The frame the click was aimed at is gone, or the menu has changed since
    Stale,

    // There's no menu on screen that can be navigated by clicking
    Unavailable,

    // The cursor didn't end up on the item, even after a correction
    Missed,

    // The keys couldn't be sent
    Failed
}

/// <summary>
/// Turns a click on the OSD mirror into remote keys. The keys for a move are worked out from a
/// fresh reading of the screen and sent back to back, then a single capture checks where the
/// cursor landed and publishes the result. OK is only ever pressed once the cursor is confirmed
/// on the clicked item, so a misfire can't select the wrong one. See OSD.md.
/// </summary>
public sealed class OsdNavigator(RT4K rt4k, OsdMirror mirror)
{
    // How many times a move that fell short is retried from where the cursor actually ended up
    private const int Corrections = 1;

    public async Task<OsdClickResult> ClickAsync(long revision, int x, int y, OsdGesture gesture, CancellationToken token)
    {
        // Resolved against the frame the user was looking at, not whatever is newest
        if (mirror.FindFrame(revision) is not OsdFrame frame)
        {
            return OsdClickResult.Stale;
        }

        if (frame.Menu is not OsdMenu seen)
        {
            return OsdClickResult.Unavailable;
        }

        OsdAction action = OsdNavigation.Resolve(seen, x, y, gesture);

        if (action.Kind == OsdActionKind.None || action.Target is not OsdItem target)
        {
            return OsdClickResult.Ignored;
        }

        // Waiting for a turn is the only part a closed page cancels. Once keys start going out
        // the click is seen through, so the mirror never stops on a half-finished move.
        OsdClickResult result = await mirror.ExclusiveAsync(() => RunAsync(seen, action.Kind, target), token);

        if (result is OsdClickResult.Missed or OsdClickResult.Failed || Program.Settings.VerboseLogging)
        {
            Console.WriteLine($"OSD click: {action.Kind} to \"{target.Label}\" (row {target.Row}): {result}");
        }

        return result;
    }

    private async Task<OsdClickResult> RunAsync(OsdMenu seen, OsdActionKind kind, OsdItem seenTarget)
    {
        // The screen may have moved on since that frame, from an earlier click or the real remote
        if (await mirror.ReadMenuAsync(CancellationToken.None) is not OsdMenu current)
        {
            return OsdClickResult.Unavailable;
        }

        if (OsdNavigation.Relocate(seen, seenTarget, current) is not OsdItem target)
        {
            return OsdClickResult.Stale;
        }

        if (kind is OsdActionKind.Left or OsdActionKind.Right)
        {
            // Left/Right act on whatever is selected, so the click only still means the same
            // thing if the cursor hasn't moved off the row it was aimed at
            if (target.Row != current.Selected.Row || !target.HasValue)
            {
                return OsdClickResult.Stale;
            }

            return await SendAndPublishAsync(kind == OsdActionKind.Left ? RT4K.Remote.Left : RT4K.Remote.Right);
        }

        OsdClickResult moved = await MoveAsync(current, target);

        if (moved != OsdClickResult.Done || kind != OsdActionKind.MoveAndSelect)
        {
            return moved;
        }

        return await SendAndPublishAsync(RT4K.Remote.OK);
    }

    /// <summary>
    /// Sends the keys that reach <paramref name="target"/>, then checks the result with one capture.
    /// If the cursor fell short, it's corrected from where it actually ended up.
    /// </summary>
    private async Task<OsdClickResult> MoveAsync(OsdMenu menu, OsdItem target)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (menu.Selected.Row == target.Row)
            {
                return OsdClickResult.Done;
            }

            if (attempt > Corrections)
            {
                return OsdClickResult.Missed;
            }

            if (!await SendAsync(OsdNavigation.PlanMove(menu, target)))
            {
                await PublishAsync();
                return OsdClickResult.Failed;
            }

            // This capture is what the page shows next, so the check costs nothing extra
            OsdFrame? after = await PublishAsync();

            if (after?.Menu is not OsdMenu next || OsdNavigation.Relocate(menu, target, next) is not OsdItem moved)
            {
                return OsdClickResult.Missed;
            }

            menu = next;
            target = moved;
        }
    }

    private async Task<OsdClickResult> SendAndPublishAsync(RT4K.Remote key)
    {
        bool sent = await SendAsync([key]);
        await PublishAsync();

        return sent ? OsdClickResult.Done : OsdClickResult.Failed;
    }

    private async Task<bool> SendAsync(IReadOnlyList<RT4K.Remote> keys)
    {
        try
        {
            return await rt4k.SendRemoteSequenceAsync(keys);
        }
        catch (Exception ex) when (ex is SerialException or IOException)
        {
            Console.WriteLine($"OSD click: couldn't send remote keys: {ex.Message}");
            return false;
        }
    }

    private async Task<OsdFrame?> PublishAsync()
    {
        OsdFrame? frame = await mirror.RefreshAsync(CancellationToken.None);

        // Nothing new was captured, so leave it to the capture loop to try again shortly
        if (frame == null)
        {
            mirror.Invalidate();
        }

        return frame;
    }
}
