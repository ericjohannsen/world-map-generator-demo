using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WorldGen;

/// <summary>
/// Minimal PNG encoder (8-bit grayscale, 16-bit grayscale, 8-bit RGB). No external dependencies.
/// </summary>
public static class PngWriter
{
    private const byte ColorTypeGrayscale = 0;
    private const byte ColorTypeRgb = 2;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Writes a single-channel 8-bit grayscale PNG.</summary>
    public static void WriteGrayscale(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        ValidateLength(pixels.Length, width, height, bytesPerPixel: 1);
        WriteImage(path, width, height, bitDepth: 8, ColorTypeGrayscale, BuildScanlines(pixels, width, height, 1));
    }

    /// <summary>
    /// Writes a single-channel 16-bit grayscale PNG. Use this for heightmaps: 8-bit samples quantize meters into
    /// steps coarse enough to create false terraces in flow routing and erosion.
    /// </summary>
    public static void WriteGrayscale16(string path, ReadOnlySpan<ushort> pixels, int width, int height)
    {
        ValidateLength(pixels.Length, width, height, bytesPerPixel: 1);

        var samples = new byte[pixels.Length * 2];
        for (int i = 0; i < pixels.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(samples.AsSpan(i * 2), pixels[i]);
        }

        WriteImage(path, width, height, bitDepth: 16, ColorTypeGrayscale, BuildScanlines(samples, width, height, 2));
    }

    /// <summary>Writes an 8-bit RGB PNG from interleaved red/green/blue samples.</summary>
    public static void WriteRgb(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        ValidateLength(pixels.Length, width, height, bytesPerPixel: 3);
        using var file = File.Create(path);
        WriteImageToStream(file, width, height, bitDepth: 8, ColorTypeRgb, BuildScanlines(pixels, width, height, 3));
    }

    /// <summary>Encodes an 8-bit RGB PNG in memory from interleaved red/green/blue samples.</summary>
    public static byte[] EncodeRgb(ReadOnlySpan<byte> pixels, int width, int height)
    {
        ValidateLength(pixels.Length, width, height, bytesPerPixel: 3);
        using var memory = new MemoryStream();
        WriteImageToStream(memory, width, height, bitDepth: 8, ColorTypeRgb, BuildScanlines(pixels, width, height, 3));
        return memory.ToArray();
    }

    private static void WriteImage(string path, int width, int height, byte bitDepth, byte colorType, byte[] scanlines)
    {
        using var file = File.Create(path);
        WriteImageToStream(file, width, height, bitDepth, colorType, scanlines);
    }

    private static void WriteImageToStream(Stream stream, int width, int height, byte bitDepth, byte colorType, byte[] scanlines)
    {
        stream.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter method: adaptive
        header[12] = 0; // interlace: none
        WriteChunk(stream, "IHDR", header);

        WriteChunk(stream, "IDAT", Compress(scanlines));
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static byte[] BuildScanlines(ReadOnlySpan<byte> samples, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        int scanlineLength = stride + 1;
        var scanlines = new byte[height * scanlineLength];

        // Each scanline is prefixed with a filter byte. Filter 0 (None) keeps the encoder simple.
        for (int y = 0; y < height; y++)
        {
            samples.Slice(y * stride, stride).CopyTo(scanlines.AsSpan(y * scanlineLength + 1));
        }

        return scanlines;
    }

    private static byte[] Compress(byte[] scanlines)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);

        uint crc = Crc32.Compute(typeBytes, data);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);

        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);
        stream.Write(crcBytes);
    }

    private static void ValidateLength(int length, int width, int height, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        long expected = (long)width * height * bytesPerPixel;
        if (length != expected)
        {
            throw new ArgumentException($"Expected {expected} samples, but got {length}.");
        }
    }
}

/// <summary>
/// CRC-32 as specified by the PNG format (polynomial 0xEDB88320, reflected).
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Update(crc, first);
        crc = Update(crc, second);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (int i = 0; i < table.Length; i++)
        {
            uint value = (uint)i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
