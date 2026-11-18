namespace rt4k_pi;

/// <summary>
/// A copy of one primary OSD plane's raw cells, so a frame can still be analysed after later
/// captures have replaced it.
/// </summary>
public sealed class OsdGrid
{
    private readonly byte[] text;
    private readonly byte[] color;

    public OsdGrid(byte[] text, byte[] color, int rows, int cols, int stride)
    {
        this.text = (byte[])text.Clone();
        this.color = (byte[])color.Clone();
        Rows = rows;
        Cols = cols;
        Stride = stride;
    }

    public int Rows { get; }
    public int Cols { get; }
    public int Stride { get; }

    /// <summary>The glyph index in a cell, or 0 for anything outside the meaningful area.</summary>
    public byte Glyph(int row, int col) => Cell(text, row, col);

    /// <summary>The cell's background mode: 0 black, 1 white, 2 green, 3 red.</summary>
    public int Background(int row, int col) => (Cell(color, row, col) >> 6) & 3;

    private byte Cell(byte[] plane, int row, int col)
    {
        if (row < 0 || col < 0 || row >= Rows || col >= Cols)
        {
            return 0;
        }

        int cell = row * Stride + col;
        return cell < plane.Length ? plane[cell] : (byte)0;
    }
}

public enum OsdMenuKind
{
    // The advanced menu, its submenus and the quick menus: fixed layout, Up/Down wrap around
    Tree,

    // The file picker: a scrolling list that keeps the cursor near the middle
    Browser
}

/// <summary>A row the cursor can land on.</summary>
/// <param name="ValueStart">First column of the setting's value, or -1 if the row has none.</param>
/// <param name="ValueEnd">Last column of the setting's value, or -1 if the row has none.</param>
public sealed record OsdItem(int Row, string Label, int ValueStart, int ValueEnd)
{
    public bool HasValue => ValueStart >= 0;
}

/// <summary>A navigable menu recognised on the primary plane, with exactly one selected item.</summary>
public sealed class OsdMenu(OsdMenuKind kind, int rows, int cols, IReadOnlyList<OsdItem> items, int selectedIndex)
{
    public OsdMenuKind Kind { get; } = kind;
    public int Rows { get; } = rows;
    public int Cols { get; } = cols;

    /// <summary>Items in the order Up/Down step through them, which is top to bottom.</summary>
    public IReadOnlyList<OsdItem> Items { get; } = items;

    public int SelectedIndex { get; } = selectedIndex;
    public OsdItem Selected => Items[SelectedIndex];

    public OsdItem? ItemAt(int row) => Items.FirstOrDefault(item => item.Row == row);

    public int IndexOf(OsdItem item)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Row == item.Row)
            {
                return i;
            }
        }

        return -1;
    }
}

public enum OsdGesture
{
    // The first click of a sequence
    Click,

    // A second or later click in quick succession
    Repeat,

    // The browser's double-click, which follows the first two clicks
    Double
}

public enum OsdActionKind
{
    None,
    Move,
    MoveAndSelect,
    Left,
    Right
}

public readonly record struct OsdAction(OsdActionKind Kind, OsdItem? Target)
{
    public static OsdAction Nothing => new(OsdActionKind.None, null);
}

/// <summary>
/// Recognises menus on the raw primary plane and works out which keys reach a clicked item. The
/// rules come from watching real hardware and are written up in OSD.md.
/// </summary>
public static class OsdNavigation
{
    /// <summary>Tree menu settings show their value from this column.</summary>
    public const int ValueColumn = 22;

    private const byte Space = 0x20;

    // Tree menus: column 1 of every item row is a branch of the tree drawing
    private const byte BranchGlyph = 0xC3;
    private const byte LastBranchGlyph = 0xC0;
    private const byte GroupGlyph = 0xE2;

    // File browser: column 2 is the entry's icon, column 0 the cursor or a scroll arrow
    private const byte FolderGlyph = 0xBF;
    private const byte FileGlyph = 0xCF;
    private const byte CursorGlyph = 0xAF;
    private const byte ScrollUpGlyph = 0x18;
    private const byte ScrollDownGlyph = 0x19;

    /// <summary>
    /// Finds the menu on a primary plane, or null when it isn't a menu we can safely navigate:
    /// nothing recognisable, a mix of layouts, or anything other than exactly one selected item.
    /// </summary>
    public static OsdMenu? Analyze(OsdGrid grid)
    {
        var tree = new List<OsdItem>();
        var browser = new List<OsdItem>();

        for (int row = 0; row < grid.Rows; row++)
        {
            if (IsTreeItem(grid, row))
            {
                tree.Add(TreeItem(grid, row));
            }
            else if (IsBrowserItem(grid, row))
            {
                browser.Add(new OsdItem(row, Text(grid, row, 4, grid.Cols), -1, -1));
            }
        }

        // Neither layout, or both at once, which has never been seen: don't guess
        if ((tree.Count > 0) == (browser.Count > 0))
        {
            return null;
        }

        List<OsdItem> items = tree.Count > 0 ? tree : browser;
        int selected = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (IsHighlighted(grid, items[i].Row))
            {
                if (selected >= 0)
                {
                    return null;
                }

                selected = i;
            }
        }

        if (selected < 0)
        {
            return null;
        }

        return new OsdMenu(tree.Count > 0 ? OsdMenuKind.Tree : OsdMenuKind.Browser, grid.Rows, grid.Cols, items, selected);
    }

    /// <summary>
    /// Works out what a click at pixel (<paramref name="x"/>, <paramref name="y"/>) of the primary
    /// plane means for <paramref name="menu"/>, the frame the user was looking at.
    /// </summary>
    public static OsdAction Resolve(OsdMenu menu, int x, int y, OsdGesture gesture)
    {
        if (x < 0 || y < 0)
        {
            return OsdAction.Nothing;
        }

        int col = x / OsdRenderer.GlyphWidth;
        int row = y / OsdRenderer.GlyphHeight;

        if (col >= menu.Cols || row >= menu.Rows || menu.ItemAt(row) is not OsdItem item)
        {
            return OsdAction.Nothing;
        }

        if (IsValueZone(menu, item, col))
        {
            // Value clicks act on every click, so a double-click has already been handled as two
            if (gesture == OsdGesture.Double)
            {
                return OsdAction.Nothing;
            }

            int start = item.ValueStart * OsdRenderer.GlyphWidth;
            int end = (item.ValueEnd + 1) * OsdRenderer.GlyphWidth;

            return new OsdAction(x * 2 < start + end ? OsdActionKind.Left : OsdActionKind.Right, item);
        }

        // A repeat is the second half of a double-click, which arrives separately
        return gesture switch
        {
            OsdGesture.Click => new OsdAction(OsdActionKind.Move, item),
            OsdGesture.Double => new OsdAction(OsdActionKind.MoveAndSelect, item),
            _ => OsdAction.Nothing
        };
    }

    /// <summary>
    /// True when a click in this column changes the item's value rather than moving to it: only
    /// on the selected row of a tree menu, and only from where the value starts.
    /// </summary>
    public static bool IsValueZone(OsdMenu menu, OsdItem item, int col)
        => menu.Kind == OsdMenuKind.Tree && item.HasValue && item.Row == menu.Selected.Row && col >= item.ValueStart;

    /// <summary>
    /// Finds the item the user clicked in <paramref name="seen"/> again in a newer reading of the
    /// screen, or null if it can't be identified with certainty.
    /// </summary>
    public static OsdItem? Relocate(OsdMenu seen, OsdItem target, OsdMenu current)
    {
        if (current.Kind != seen.Kind)
        {
            return null;
        }

        if (seen.Kind == OsdMenuKind.Tree)
        {
            // Tree menus don't scroll and their labels don't change with the values, so the same
            // labels in the same order means the same menu, and the item is at the same position.
            if (current.Items.Count != seen.Items.Count)
            {
                return null;
            }

            for (int i = 0; i < seen.Items.Count; i++)
            {
                if (seen.Items[i].Label != current.Items[i].Label)
                {
                    return null;
                }
            }

            int index = seen.IndexOf(target);
            return index < 0 ? null : current.Items[index];
        }

        // The browser may have scrolled, and long names are cut off so two entries can look alike.
        // Line the two windows up instead: every offset at which all overlapping entries agree is a
        // way the list could have moved, and only a single such offset identifies the entry. A
        // small overlap could agree by chance in a different folder, so at least half the shorter
        // window has to line up.
        int seenIndex = seen.IndexOf(target);

        if (seenIndex < 0)
        {
            return null;
        }

        int minimumOverlap = (Math.Min(seen.Items.Count, current.Items.Count) + 1) / 2;
        OsdItem? found = null;

        for (int offset = -seen.Items.Count + 1; offset < current.Items.Count; offset++)
        {
            int targetIndex = seenIndex + offset;

            if (targetIndex < 0 || targetIndex >= current.Items.Count || !WindowsAgree(seen, current, offset, minimumOverlap))
            {
                continue;
            }

            if (found != null)
            {
                return null;
            }

            found = current.Items[targetIndex];
        }

        return found;
    }

    // True when enough entries are visible in both windows and all of them have the same name,
    // given that entry i of seen is entry i + offset of current
    private static bool WindowsAgree(OsdMenu seen, OsdMenu current, int offset, int minimumOverlap)
    {
        int overlap = 0;

        for (int i = 0; i < seen.Items.Count; i++)
        {
            int j = i + offset;

            if (j < 0 || j >= current.Items.Count)
            {
                continue;
            }

            if (seen.Items[i].Label != current.Items[j].Label)
            {
                return false;
            }

            overlap++;
        }

        return overlap >= minimumOverlap;
    }

    /// <summary>The keys that take the cursor from the selected item to <paramref name="target"/>.</summary>
    public static RT4K.Remote[] PlanMove(OsdMenu menu, OsdItem target)
    {
        int from = menu.SelectedIndex;
        int to = menu.IndexOf(target);

        if (to < 0)
        {
            throw new ArgumentException("The target isn't an item of this menu.", nameof(target));
        }

        int down;
        int up;

        if (menu.Kind == OsdMenuKind.Tree)
        {
            // Up/Down wrap around, so go whichever way is shorter
            int count = menu.Items.Count;
            down = (to - from + count) % count;
            up = (from - to + count) % count;
        }
        else
        {
            // Visible browser entries are consecutive, even if the list scrolls on the way.
            // Wrapping wasn't tested, so it's never relied on.
            down = Math.Max(0, to - from);
            up = Math.Max(0, from - to);
        }

        return down <= up
            ? Enumerable.Repeat(RT4K.Remote.Down, down).ToArray()
            : Enumerable.Repeat(RT4K.Remote.Up, up).ToArray();
    }

    private static bool IsTreeRow(OsdGrid grid, int row)
        => grid.Glyph(row, 0) == Space
            && grid.Glyph(row, 1) is BranchGlyph or LastBranchGlyph or GroupGlyph
            && grid.Glyph(row, 2) == Space
            && IsVisible(grid.Glyph(row, 3));

    // The group glyph is used both for section headers and for standalone items; it's a header
    // exactly when the rows beneath it are its branches.
    private static bool IsTreeItem(OsdGrid grid, int row)
        => IsTreeRow(grid, row)
            && !(grid.Glyph(row, 1) == GroupGlyph
                && IsTreeRow(grid, row + 1)
                && grid.Glyph(row + 1, 1) is BranchGlyph or LastBranchGlyph);

    private static OsdItem TreeItem(OsdGrid grid, int row)
    {
        // A setting pads its label out so the value starts in a fixed column. A plain item whose
        // label happens to run past that column has no gap just before it.
        bool hasValue = grid.Cols > ValueColumn
            && !IsVisible(grid.Glyph(row, ValueColumn - 1))
            && IsVisible(grid.Glyph(row, ValueColumn));

        if (!hasValue)
        {
            return new OsdItem(row, Text(grid, row, 3, grid.Cols), -1, -1);
        }

        int end = grid.Cols - 1;

        while (end > ValueColumn && !IsVisible(grid.Glyph(row, end)))
        {
            end--;
        }

        return new OsdItem(row, Text(grid, row, 3, ValueColumn), ValueColumn, end);
    }

    private static bool IsBrowserItem(OsdGrid grid, int row)
        => grid.Glyph(row, 0) is Space or CursorGlyph or ScrollUpGlyph or ScrollDownGlyph
            && grid.Glyph(row, 1) == Space
            && grid.Glyph(row, 2) is FolderGlyph or FileGlyph
            && grid.Glyph(row, 3) == Space
            && IsVisible(grid.Glyph(row, 4));

    // The cursor paints the whole row's background white, whatever colour the text is
    private static bool IsHighlighted(OsdGrid grid, int row)
    {
        for (int col = 0; col < grid.Cols; col++)
        {
            if (grid.Background(row, col) != 1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVisible(byte glyph) => glyph != Space && glyph != 0;

    // Glyph indices aren't ASCII outside the printable range, but labels are only ever compared
    // with each other, so mapping each byte straight to a char is enough to tell them apart.
    private static string Text(OsdGrid grid, int row, int start, int end)
    {
        var chars = new char[Math.Max(0, end - start)];

        for (int col = start; col < end; col++)
        {
            byte glyph = grid.Glyph(row, col);
            chars[col - start] = glyph == 0 ? ' ' : (char)glyph;
        }

        return new string(chars).Trim();
    }
}
