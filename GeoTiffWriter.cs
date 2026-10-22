using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WorldGen;

public sealed record GeoTiffOptions
{
    /// <summary>Longitude of the western edge of the raster.</summary>
    public double WestLongitude { get; init; } = -180.0;

    /// <summary>Latitude of the northern edge of the raster.</summary>
    public double NorthLatitude { get; init; } = 90.0;

    public double LongitudeSpan { get; init; } = 360.0;

    public double LatitudeSpan { get; init; } = 180.0;

    /// <summary>Radius written as the geographic CRS semi-major axis, in metres.</summary>
    public double PlanetRadiusMeters { get; init; } = 6_371_000.0;

    public string Description { get; init; } = "WorldGen elevations above sea level";
}

/// <summary>
/// Minimal, dependency-free writer for single-band float32 GeoTIFFs.
/// </summary>
/// <remarks>
/// Produces a classic (non-Big) TIFF: little-endian, Deflate-compressed strips, one float sample per pixel,
/// geographic coordinates in degrees with a user-defined spherical datum. Samples are pixel-centre elevations;
/// the raster is tagged as area-registered (PixelIsArea) with the tiepoint on the north-west corner, which is the
/// convention DEM readers expect and avoids the PixelIsPoint half-pixel ambiguity.
/// </remarks>
public static class GeoTiffWriter
{
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeDouble = 12;

    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometricInterpretation = 262;
    private const ushort TagImageDescription = 270;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagSoftware = 305;
    private const ushort TagSampleFormat = 339;
    private const ushort TagModelPixelScale = 33550;
    private const ushort TagModelTiepoint = 33922;
    private const ushort TagGeoKeyDirectory = 34735;
    private const ushort TagGeoDoubleParams = 34736;
    private const ushort TagGeoAsciiParams = 34737;

    public static void WriteFloat32(string path, ReadOnlySpan<float> values, int width, int height, GeoTiffOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.LongitudeSpan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.LatitudeSpan);

        if (values.Length != (long)width * height)
        {
            throw new ArgumentException($"Expected {width * height} samples, but got {values.Length}.", nameof(values));
        }

        int bytesPerRow = width * 4;
        int rowsPerStrip = Math.Clamp(65_536 / bytesPerRow, 1, height);
        int stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;

        var strips = new byte[stripCount][];
        for (int i = 0; i < stripCount; i++)
        {
            int firstRow = i * rowsPerStrip;
            int rows = Math.Min(rowsPerStrip, height - firstRow);
            strips[i] = CompressSamples(values.Slice(firstRow * width, rows * width));
        }

        long stripOffset = 8;
        var stripOffsets = new uint[stripCount];
        for (int i = 0; i < stripCount; i++)
        {
            stripOffsets[i] = (uint)stripOffset;
            stripOffset += strips[i].Length;
        }

        long ifdOffset = stripOffset;
        var entries = BuildEntries(strips, stripOffsets, width, height, rowsPerStrip, options);
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        long ifdSize = 2 + entries.Count * 12L + 4;
        long extraStart = Align(stripOffset + ifdSize, 8);
        long cursor = extraStart;
        var externalOffsets = new long[entries.Count];

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Value.Length > 4)
            {
                externalOffsets[i] = cursor;
                cursor += Align(entries[i].Value.Length, 8);
            }
        }

        if (cursor > uint.MaxValue)
        {
            throw new NotSupportedException(
                "The raster is too large for a classic TIFF. BigTIFF support is required beyond 4 GB.");
        }

        using var file = File.Create(path);

        Span<byte> header = stackalloc byte[8];
        header[0] = (byte)'I';
        header[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)ifdOffset);
        file.Write(header);

        foreach (byte[] strip in strips)
        {
            file.Write(strip);
        }

        Span<byte> scratch = stackalloc byte[4];
        Span<byte> inlineValue = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(scratch, (ushort)entries.Count);
        file.Write(scratch[..2]);

        for (int i = 0; i < entries.Count; i++)
        {
            IfdEntry entry = entries[i];

            BinaryPrimitives.WriteUInt16LittleEndian(scratch, entry.Tag);
            file.Write(scratch[..2]);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch, entry.Type);
            file.Write(scratch[..2]);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, entry.Count);
            file.Write(scratch);

            if (entry.Value.Length <= 4)
            {
                inlineValue.Clear();
                entry.Value.CopyTo(inlineValue);
                file.Write(inlineValue);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(scratch, (uint)externalOffsets[i]);
                file.Write(scratch);
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(scratch, 0); // no next IFD
        file.Write(scratch);

        while (file.Position < extraStart)
        {
            file.WriteByte(0);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Value.Length <= 4)
            {
                continue;
            }

            file.Write(entries[i].Value);

            for (long pad = entries[i].Value.Length; pad < Align(entries[i].Value.Length, 8); pad++)
            {
                file.WriteByte(0);
            }
        }
    }

    private static List<IfdEntry> BuildEntries(
        byte[][] strips,
        uint[] stripOffsets,
        int width,
        int height,
        int rowsPerStrip,
        GeoTiffOptions options)
    {
        byte[] description = Encoding.ASCII.GetBytes(options.Description + "\0");
        byte[] software = Encoding.ASCII.GetBytes("WorldGen\0");
        byte[] citation = Encoding.ASCII.GetBytes("WorldGen spherical planet|");

        // GeoKeyDirectory: version 1.1.0, eight keys.
        // 1024 GTModelType = 2 (geographic), 1025 GTRasterType = 1 (PixelIsArea),
        // 2048 GeographicType = 32767 (user-defined), 2049 citation, 2054 angular units = degrees,
        // 2057 semi-major axis (custom radius), 2059 inverse flattening = 0 (sphere), 4099 vertical units = metres.
        ushort[] geoKeys =
        [
            1, 1, 0, 8,
            1024, 0, 1, 2,
            1025, 0, 1, 1,
            2048, 0, 1, 32767,
            2049, TagGeoAsciiParams, (ushort)citation.Length, 0,
            2054, 0, 1, 9102,
            2057, TagGeoDoubleParams, 1, 0,
            2059, TagGeoDoubleParams, 1, 1,
            4099, 0, 1, 9001,
        ];

        return
        [
            new(TagImageWidth, TypeLong, 1, EncodeLongs((uint)width)),
            new(TagImageLength, TypeLong, 1, EncodeLongs((uint)height)),
            new(TagBitsPerSample, TypeShort, 1, EncodeShorts(32)),
            new(TagCompression, TypeShort, 1, EncodeShorts(8)), // Adobe Deflate (zlib stream)
            new(TagPhotometricInterpretation, TypeShort, 1, EncodeShorts(1)), // BlackIsZero
            new(TagImageDescription, TypeAscii, (uint)description.Length, description),
            new(TagStripOffsets, TypeLong, (uint)stripOffsets.Length, EncodeLongs(stripOffsets)),
            new(TagSamplesPerPixel, TypeShort, 1, EncodeShorts(1)),
            new(TagRowsPerStrip, TypeLong, 1, EncodeLongs((uint)rowsPerStrip)),
            new(TagStripByteCounts, TypeLong, (uint)strips.Length, EncodeLongs(strips.Select(s => (uint)s.Length).ToArray())),
            new(TagPlanarConfiguration, TypeShort, 1, EncodeShorts(1)), // chunky
            new(TagSoftware, TypeAscii, (uint)software.Length, software),
            new(TagSampleFormat, TypeShort, 1, EncodeShorts(3)), // IEEE floating point
            new(TagModelPixelScale, TypeDouble, 3,
                EncodeDoubles(options.LongitudeSpan / width, options.LatitudeSpan / height, 0.0)),
            new(TagModelTiepoint, TypeDouble, 6,
                EncodeDoubles(0, 0, 0, options.WestLongitude, options.NorthLatitude, 0)),
            new(TagGeoKeyDirectory, TypeShort, (uint)geoKeys.Length, EncodeShorts(geoKeys)),
            new(TagGeoDoubleParams, TypeDouble, 2, EncodeDoubles(options.PlanetRadiusMeters, 0.0)),
            new(TagGeoAsciiParams, TypeAscii, (uint)citation.Length, citation),
        ];
    }

    private static byte[] CompressSamples(ReadOnlySpan<float> values)
    {
        var raw = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), values[i]);
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] EncodeShorts(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return bytes;
    }

    private static byte[] EncodeLongs(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static byte[] EncodeDoubles(params double[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private readonly record struct IfdEntry(ushort Tag, ushort Type, uint Count, byte[] Value);
}
