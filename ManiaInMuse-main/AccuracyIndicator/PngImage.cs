using System.IO.Compression;

namespace AccuracyIndicator;

/// <summary>
/// 极简 PNG 解码器（无 System.Drawing 依赖）。
/// 支持 8-bit、非隔行、color type 0/2/3/4/6。
/// 输出 32bpp 预乘 Alpha BGRA，可直接交给 NativeOverlayWindow.DrawImage 合成。
/// </summary>
internal sealed class PngImage
{
    internal readonly int Width;
    internal readonly int Height;
    internal readonly byte[] Bgra; // 预乘 Alpha，BGRA，行主序，自上而下

    private PngImage(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    internal static PngImage Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Decode(data, path);
    }

    internal static PngImage Decode(byte[] data, string label)
    {
        if (data.Length < 33)
            throw new InvalidDataException($"{label}: not a PNG");

        // 8 字节签名
        ReadOnlySpan<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        for (int i = 0; i < 8; i++)
        {
            if (data[i] != sig[i])
                throw new InvalidDataException($"{label}: bad PNG signature");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        var idat = new MemoryStream();
        byte[] palette = null;
        byte[] trns = null;
        bool seenIhdr = false;

        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            int len = ReadInt(data, pos);
            uint type = ReadUInt(data, pos + 4);
            int body = pos + 8;
            int end = body + len;

            if (type == 0x49484452u) // IHDR
            {
                if (len < 13)
                    throw new InvalidDataException($"{label}: bad IHDR");
                width = ReadInt(data, body);
                height = ReadInt(data, body + 4);
                bitDepth = data[body + 8];
                colorType = data[body + 9];
                int compression = data[body + 10];
                int filter = data[body + 11];
                int interlace = data[body + 12];
                if (compression != 0 || filter != 0 || interlace != 0)
                    throw new InvalidDataException($"{label}: unsupported PNG variant (compression={compression} filter={filter} interlace={interlace})");
                if (bitDepth != 8)
                    throw new InvalidDataException($"{label}: unsupported bit depth {bitDepth}");
                if (colorType is not (0 or 2 or 3 or 4 or 6))
                    throw new InvalidDataException($"{label}: unsupported color type {colorType}");
                seenIhdr = true;
            }
            else if (type == 0x504C5445u) // PLTE
            {
                palette = new byte[len];
                Array.Copy(data, body, palette, 0, len);
            }
            else if (type == 0x74524E53u) // tRNS
            {
                trns = new byte[len];
                Array.Copy(data, body, trns, 0, len);
            }
            else if (type == 0x49444154u) // IDAT
            {
                idat.Write(data, body, len);
            }
            else if (type == 0x49454E44u) // IEND
            {
                break;
            }

            pos = end + 4; // 跳过 CRC
        }

        if (!seenIhdr || width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidDataException($"{label}: missing/invalid IHDR");

        byte[] raw = Decompress(idat.ToArray(), label);

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        int bpp = channels; // 8-bit
        int stride = width * channels;
        int rawStride = stride; // PNG 行无额外对齐

        if (raw.Length < rawStride * height)
            throw new InvalidDataException($"{label}: IDAT too small");

        // 去滤波
        var unfiltered = new byte[rawStride * height];
        var prevRow = new byte[rawStride];
        var curRow = new byte[rawStride];
        for (int y = 0; y < height; y++)
        {
            int filterType = raw[y * (rawStride + 1)];
            int rowStart = y * (rawStride + 1) + 1;
            Array.Copy(raw, rowStart, curRow, 0, rawStride);

            switch (filterType)
            {
                case 0: break;
                case 1: // Sub
                    for (int i = bpp; i < rawStride; i++)
                        curRow[i] = (byte)(curRow[i] + curRow[i - bpp]);
                    break;
                case 2: // Up
                    for (int i = 0; i < rawStride; i++)
                        curRow[i] = (byte)(curRow[i] + prevRow[i]);
                    break;
                case 3: // Average
                    for (int i = 0; i < rawStride; i++)
                    {
                        int a = i >= bpp ? curRow[i - bpp] : 0;
                        int b = prevRow[i];
                        curRow[i] = (byte)(curRow[i] + ((a + b) >> 1));
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < rawStride; i++)
                    {
                        int a = i >= bpp ? curRow[i - bpp] : 0;
                        int b = prevRow[i];
                        int c = i >= bpp ? prevRow[i - bpp] : 0;
                        curRow[i] = (byte)(curRow[i] + Paeth(a, b, c));
                    }
                    break;
                default:
                    throw new InvalidDataException($"{label}: bad filter type {filterType}");
            }

            Array.Copy(curRow, 0, unfiltered, y * rawStride, rawStride);
            (prevRow, curRow) = (curRow, prevRow);
        }

        byte[] bgra = new byte[width * height * 4];
        int outPos = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * rawStride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * channels;
                byte r, g, b, a;
                switch (colorType)
                {
                    case 0: // gray
                        r = g = b = unfiltered[p];
                        a = 255;
                        break;
                    case 2: // RGB
                        r = unfiltered[p];
                        g = unfiltered[p + 1];
                        b = unfiltered[p + 2];
                        a = 255;
                        break;
                    case 3: // palette
                    {
                        int idx = unfiltered[p];
                        if (palette == null || idx * 3 + 2 >= palette.Length)
                            throw new InvalidDataException($"{label}: bad palette index");
                        r = palette[idx * 3];
                        g = palette[idx * 3 + 1];
                        b = palette[idx * 3 + 2];
                        a = trns != null && idx < trns.Length ? trns[idx] : (byte)255;
                        break;
                    }
                    case 4: // gray + alpha
                        r = g = b = unfiltered[p];
                        a = unfiltered[p + 1];
                        break;
                    default: // 6 RGBA
                        r = unfiltered[p];
                        g = unfiltered[p + 1];
                        b = unfiltered[p + 2];
                        a = unfiltered[p + 3];
                        break;
                }

                // 预乘 Alpha
                bgra[outPos] = (byte)((b * a + 127) / 255);
                bgra[outPos + 1] = (byte)((g * a + 127) / 255);
                bgra[outPos + 2] = (byte)((r * a + 127) / 255);
                bgra[outPos + 3] = a;
                outPos += 4;
            }
        }

        return new PngImage(width, height, bgra);
    }

    private static byte[] Decompress(byte[] idat, string label)
    {
        if (idat.Length < 6)
            throw new InvalidDataException($"{label}: bad zlib stream");

        // 跳过 2 字节 zlib 头，去掉尾部 4 字节 Adler-32（不校验头字节，兼容所有合法 zlib 变体）
        using var input = new MemoryStream(idat, 2, idat.Length - 6);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }

    private static int ReadInt(byte[] data, int pos)
    {
        return (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
    }

    private static uint ReadUInt(byte[] data, int pos)
    {
        return ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];
    }
}
