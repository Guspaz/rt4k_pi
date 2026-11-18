# OSD mirror and click-to-navigate

This describes how rt4k_pi reads the RT4K's on-screen menus, and how a click on the mirror on the
Remote / OSD page becomes remote key presses. Wire-level details of `osd`, `osd2`, `font` and
`remote` live in `PROTOCOL.md`. Everything below was observed on real hardware, an RT4K Pro on
firmware 1.99.11. It describes how the menus behave, not a firmware contract.

## Grid layout

The primary plane (`osd`) is 32 rows. The grid RAM stride is 64 cells, and only the first `width`
columns matter: 40 for ordinary menus and 52 for the file browser and the Profiles menu. Each cell is
an 8x16 glyph. The primary plane is drawn at the top left of the composed mirror image, so a pixel in
that region maps directly to a cell (`col = x / 8`, `row = y / 16`).

Background mode is the colour byte's top two bits (`(color >> 6) & 3`): 0 is black, 1 is white, 2 is
green and 3 is red.

## Recognising menu items

Two menu layouts exist:

**Tree menus.** These are the main menu, the advanced menu and its submenus, and the Input, Output
and Profiles menus. Every item row is `space, branch glyph, space, label`:

| Glyph at column 1 | Meaning |
|---|---|
| `0xC3` | Item with more items below it in the same group |
| `0xC0` | Last item in a group |
| `0xE2` | Group header **if the next row is a `0xC3`/`0xC0` item**, otherwise a standalone item (e.g. Input > HDMI, Main Menu > Manage Profiles) |

Settings with a value have the value starting at **column 22**, with the label padded up to it. A
value can run to the right edge of the plane. Values such as `N/A` or `[ACTIVE]` are still shown at
column 22. Greyed-out rows (e.g. `Vertical Pan: N/A`) can still be selected.

Titles, blank separators (all NUL), help text (column 0 is NUL) and the two footer rows
(`Profile: ...`, version and mode) are not items.

**File browser.** This is used by Load Banner and Load From File. Item rows are
`marker, space, icon, space, name`. The icon is `0xBF` for a folder and `0xCF` for a file. Column 0
holds `0xAF` on the selected row, and `0x18`/`0x19` on rows 2/30 when the list can scroll up/down.
Row 0 is the title and row 31 is the key help.

## Selection

The selected item is the one item row whose **every meaningful cell has a white background**
(mode 1). The foreground colour varies (`0x40`, `0x4C`, `0x4A`), so only the background mode is
checked. No other screen state marks the cursor.

## Remote key behaviour

- **Tree menus:** Up/Down move between items in row order. They skip headers, blanks and help text,
  and **wrap** from the last item to the first and vice versa. The whole menu fits on screen and
  does not scroll.
- **File browser:** Up/Down move one entry. The list scrolls so the cursor stays near the middle
  (row 16 once scrolled). Visible entries are contiguous, so moving from row `a` to row `b` takes
  `|b - a|` presses, even if the list scrolls during the move. Wrapping was not tested, so it is
  never relied on.
- OK on an item enters its submenu, opening it with the first item selected. Back returns to the
  parent with the cursor on the item that was entered.
- Left/Right change the selected row's value.

## Key pacing

`remote` is a single-slot latch (see `PROTOCOL.md`). Each `remote` command's echo round trip takes
about 10 ms. In testing, bursts of 15 keys with 0, 5, 10, 20 and 40 ms gaps after each echo all
landed on the correct row, so the latch is consumed faster than one command round trip. rt4k_pi
still leaves a 15 ms gap after each echo as margin. A full-plane `osd` read taken right after the
last echo already shows the final cursor position.

## Click handling

1. The page receives each mirror frame along with the item rows, the selected row and value spans
   for that frame's revision. It uses these to outline the row under the pointer.
2. A click is sent as the frame revision, the pixel position and a gesture (click, repeated click or
   double-click). The server looks up the item in the frame the user actually saw, then rereads the
   primary plane and finds the same item again by label, preferring the same row. If the item can't
   be identified with certainty, the click is refused rather than guessed.
3. The key sequence is calculated from the fresh grid and sent paced, without waiting for a frame
   between keys.
4. One capture afterwards checks where the cursor ended up and publishes the new frame. If the cursor
   missed, one correction is calculated from that capture. OK is only pressed after the cursor is
   confirmed on the target, so a misfire can never select the wrong item.

Gestures:

| Where | Click | Double-click |
|---|---|---|
| Any item that isn't selected, or the label of the selected item | Move to it | Move to it, then OK |
| Value of the selected tree item, left half | Left (every click, including rapid repeats) | Ignored |
| Value of the selected tree item, right half (including blank space after the value) | Right | Ignored |

Repeated clicks on a label are ignored, because they are the first half of a double-click. Only
the primary plane can be clicked. The secondary message plane, the on-screen keyboard and
non-menu screens (no selected item found) are not clickable.
