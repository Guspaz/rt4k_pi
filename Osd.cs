namespace rt4k_pi;

using System.IO.Compression;

// Server-side mirror of the RT4K's on-screen display (see PROTOCOL-GUIDE.md section 10).
//
// The device exposes three surfaces: the primary cell grid (osd), a secondary plane used for
// resolution/no-signal messages and the on-screen keyboard (osd2), and a user-selected BMP
// banner. We pull them over the binary plane, rasterise them here, and hand the page a single
// finished PNG so the browser has no rendering logic at all.
//
// The project is AOT-published with no image library available, so the PNG writer and BMP
// reader below are hand-rolled.

/// <summary>A simple 32-bit RGBA image buffer.</summary>
public sealed class Bitmap(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    // RGBA, row-major, 4 bytes per pixel
    public byte[] Pixels { get; } = new byte[width * height * 4];

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        int i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    /// <summary>Copies <paramref name="source"/> onto this image, skipping fully transparent pixels.</summary>
    public void Blit(Bitmap source, int destX, int destY, int scale = 1)
    {
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int i = (y * source.Width + x) * 4;

                if (source.Pixels[i + 3] == 0)
                {
                    continue;
                }

                byte r = source.Pixels[i];
                byte g = source.Pixels[i + 1];
                byte b = source.Pixels[i + 2];

                for (int sy = 0; sy < scale; sy++)
                {
                    for (int sx = 0; sx < scale; sx++)
                    {
                        SetPixel(destX + x * scale + sx, destY + y * scale + sy, r, g, b);
                    }
                }
            }
        }
    }

    /// <summary>Multiplies every pixel's brightness, used to dim a stale frame.</summary>
    public void Dim(double factor)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = (byte)(Pixels[i] * factor);
            Pixels[i + 1] = (byte)(Pixels[i + 1] * factor);
            Pixels[i + 2] = (byte)(Pixels[i + 2] * factor);
        }
    }
}

public static class Png
{
    private static readonly uint[] crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFF;
    }

    /// <summary>Encodes an RGBA bitmap as a PNG.</summary>
    public static byte[] Encode(Bitmap image)
    {
        using var output = new MemoryStream();

        // Signature
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: 8 bits per channel, colour type 6 (truecolour with alpha)
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)image.Width);
        WriteBigEndian(header, 4, (uint)image.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        // Each scanline is prefixed with its filter type; 0 means no filtering, which costs a
        // little size but keeps this simple and fast.
        int stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];

        for (int y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var compressed = new MemoryStream();

        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);

        var typeAndData = new byte[4 + data.Length];
        typeAndData[0] = (byte)type[0];
        typeAndData[1] = (byte)type[1];
        typeAndData[2] = (byte)type[2];
        typeAndData[3] = (byte)type[3];
        data.CopyTo(typeAndData, 4);
        output.Write(typeAndData);

        Span<byte> crc = stackalloc byte[4];
        WriteBigEndian(crc, 0, Crc32(typeAndData));
        output.Write(crc);
    }

    private static void WriteBigEndian(Span<byte> target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}

public static class Bmp
{
    /// <summary>
    /// Decodes the 24/32-bit BMP formats the RT4K accepts for its banner. Magenta (FF00FF) is
    /// the firmware's transparency key, so those pixels come back fully transparent.
    /// </summary>
    public static Bitmap? Decode(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
        {
            return null;
        }

        int pixelOffset = BitConverter.ToInt32(data, 10);
        int headerSize = BitConverter.ToInt32(data, 14);
        int width = BitConverter.ToInt32(data, 18);
        int height = BitConverter.ToInt32(data, 22);
        short bpp = BitConverter.ToInt16(data, 28);

        if (headerSize < 40 || width <= 0 || (bpp != 24 && bpp != 32))
        {
            return null;
        }

        // A negative height means the rows are stored top-down instead of the usual bottom-up
        bool topDown = height < 0;
        height = Math.Abs(height);

        int bytesPerPixel = bpp / 8;

        // BMP rows are padded to a 4 byte boundary
        int stride = (width * bytesPerPixel + 3) & ~3;

        if (pixelOffset + stride * height > data.Length)
        {
            return null;
        }

        var image = new Bitmap(width, height);

        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            int rowStart = pixelOffset + sourceRow * stride;

            for (int x = 0; x < width; x++)
            {
                int i = rowStart + x * bytesPerPixel;

                // BMP stores BGR(A)
                byte b = data[i];
                byte g = data[i + 1];
                byte r = data[i + 2];

                bool transparent = r == 0xFF && g == 0x00 && b == 0xFF;
                image.SetPixel(x, y, r, g, b, transparent ? (byte)0 : (byte)255);
            }
        }

        return image;
    }
}

/// <summary>Rasterises an OSD cell grid through the device's custom glyph ROM.</summary>
public static class OsdRenderer
{
    public const int GlyphWidth = 8;
    public const int GlyphHeight = 16;

    // The grid RAM is always 64 columns wide regardless of how many are meaningful
    public const int Stride = 64;

    // 2 bits per channel in the colour byte, so scale 0-3 up to 0-255
    private const int ChannelScale = 85;

    /// <summary>
    /// Renders <paramref name="rows"/> x <paramref name="cols"/> cells of a text/color plane.
    /// </summary>
    /// <remarks>
    /// Layout per PROTOCOL-GUIDE.md: the font is row-major (font[row * 256 + glyph]) and each
    /// byte is 8 horizontal pixels with the LSB at the left. The colour byte packs a 2-bit
    /// foreground as [5:4]=R [3:2]=G [1:0]=B and a background mode in [7:6].
    /// </remarks>
    public static Bitmap Render(byte[] text, byte[] color, byte[] font, int rows, int cols, int stride = Stride)
    {
        var image = new Bitmap(cols * GlyphWidth, rows * GlyphHeight);

        for (int cy = 0; cy < rows; cy++)
        {
            for (int cx = 0; cx < cols; cx++)
            {
                int cell = cy * stride + cx;

                if (cell >= text.Length || cell >= color.Length)
                {
                    continue;
                }

                byte glyph = text[cell];
                byte col = color[cell];

                byte fgR = (byte)(((col >> 4) & 3) * ChannelScale);
                byte fgG = (byte)(((col >> 2) & 3) * ChannelScale);
                byte fgB = (byte)((col & 3) * ChannelScale);

                var (bgR, bgG, bgB) = BackgroundColor((col >> 6) & 3);

                for (int row = 0; row < GlyphHeight; row++)
                {
                    int fontIndex = row * 256 + glyph;

                    if (fontIndex >= font.Length)
                    {
                        continue;
                    }

                    byte bits = font[fontIndex];

                    for (int x = 0; x < GlyphWidth; x++)
                    {
                        bool on = ((bits >> x) & 1) != 0;

                        image.SetPixel(
                            cx * GlyphWidth + x,
                            cy * GlyphHeight + row,
                            on ? fgR : bgR,
                            on ? fgG : bgG,
                            on ? fgB : bgB);
                    }
                }
            }
        }

        return image;
    }

    private static (byte R, byte G, byte B) BackgroundColor(int mode) => mode switch
    {
        1 => (255, 255, 255),
        2 => (0, 255, 0),
        3 => (255, 0, 0),
        _ => (0, 0, 0)
    };
}
