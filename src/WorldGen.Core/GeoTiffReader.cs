using System.Buffers.Binary;
using System.IO.Compression;

namespace WorldGen;

public sealed record LoadedElevationMap(
    Heightmap Map,
    float PlanetRadiusMeters,
    string SourceGeoTiffPath,
    WorldManifest? SourceManifest);

/// <summary>
/// Dependency-free reader for single-band float32 GeoTIFF files written by <see cref="GeoTiffWriter"/>.
/// Opens the file strictly read-only so source elevation artifacts are never modified.
/// </summary>
public static class GeoTiffReader
{
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeDouble = 12;

    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagSampleFormat = 339;
    private const ushort TagModelPixelScale = 33550;
    private const ushort TagModelTiepoint = 33922;
    private const ushort TagGeoDoubleParams = 34736;

    public static LoadedElevationMap ReadFloat32(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"GeoTIFF file not found: {fullPath}", fullPath);
        }

        byte[] fileBytes = File.ReadAllBytes(fullPath);
        if (fileBytes.Length < 8 || fileBytes[0] != (byte)'I' || fileBytes[1] != (byte)'I')
        {
            throw new InvalidDataException("Expected a little-endian ('II') classic TIFF file.");
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(2, 2));
        if (magic != 42)
        {
            throw new InvalidDataException($"Unsupported TIFF magic number {magic} (expected 42).");
        }

        uint ifdOffset = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(4, 4));
        if (ifdOffset + 2 > fileBytes.Length)
        {
            throw new InvalidDataException("Invalid IFD offset in GeoTIFF header.");
        }

        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan((int)ifdOffset, 2));
        int cursor = (int)ifdOffset + 2;

        int width = 0;
        int height = 0;
        int bitsPerSample = 32;
        int compression = 8;
        int samplesPerPixel = 1;
        int sampleFormat = 3;
        int rowsPerStrip = 0;
        uint[]? stripOffsets = null;
        uint[]? stripByteCounts = null;
        double[]? pixelScale = null;
        double[]? tiepoint = null;
        double[]? geoDoubles = null;

        for (int i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> entry = fileBytes.AsSpan(cursor, 12);
            cursor += 12;

            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(entry[..2]);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(2, 2));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
            ReadOnlySpan<byte> valueField = entry.Slice(8, 4);

            switch (tag)
            {
                case TagImageWidth:
                    width = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagImageLength:
                    height = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagBitsPerSample:
                    bitsPerSample = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagCompression:
                    compression = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagSamplesPerPixel:
                    samplesPerPixel = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagSampleFormat:
                    sampleFormat = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagRowsPerStrip:
                    rowsPerStrip = (int)ReadScalarUInt(fileBytes, type, count, valueField);
                    break;
                case TagStripOffsets:
                    stripOffsets = ReadUIntArray(fileBytes, type, count, valueField);
                    break;
                case TagStripByteCounts:
                    stripByteCounts = ReadUIntArray(fileBytes, type, count, valueField);
                    break;
                case TagModelPixelScale:
                    pixelScale = ReadDoubleArray(fileBytes, type, count, valueField);
                    break;
                case TagModelTiepoint:
                    tiepoint = ReadDoubleArray(fileBytes, type, count, valueField);
                    break;
                case TagGeoDoubleParams:
                    geoDoubles = ReadDoubleArray(fileBytes, type, count, valueField);
                    break;
            }
        }

        if (width < 1 || height < 1)
            throw new InvalidDataException($"Invalid GeoTIFF dimensions: {width}x{height}.");
        if (bitsPerSample != 32 || sampleFormat != 3 || samplesPerPixel != 1)
            throw new InvalidDataException("Expected single-band 32-bit IEEE float GeoTIFF.");
        if (compression != 8 && compression != 1)
            throw new InvalidDataException($"Unsupported TIFF compression {compression} (expected 8 = Deflate or 1 = None).");
        if (stripOffsets is null || stripByteCounts is null || stripOffsets.Length != stripByteCounts.Length)
            throw new InvalidDataException("Missing or mismatched StripOffsets / StripByteCounts tags.");

        if (rowsPerStrip <= 0)
        {
            rowsPerStrip = height;
        }

        MapView view = MapView.WholePlanet;
        if (pixelScale is { Length: >= 2 } && tiepoint is { Length: >= 6 })
        {
            double west = tiepoint[3];
            double north = tiepoint[4];
            double lonSpan = pixelScale[0] * width;
            double latSpan = pixelScale[1] * height;
            view = new MapView(north, north - latSpan, west, west + lonSpan);
        }

        float planetRadius = 6_371_000f;
        if (geoDoubles is { Length: >= 1 } && geoDoubles[0] > 1_000.0)
        {
            planetRadius = (float)geoDoubles[0];
        }

        var map = new Heightmap(width, height, view);
        Span<float> dest = map.Elevations;

        for (int stripIndex = 0; stripIndex < stripOffsets.Length; stripIndex++)
        {
            int firstRow = stripIndex * rowsPerStrip;
            int rows = Math.Min(rowsPerStrip, height - firstRow);
            int expectedSamples = rows * width;
            int expectedBytes = expectedSamples * 4;

            int offset = (int)stripOffsets[stripIndex];
            int byteCount = (int)stripByteCounts[stripIndex];

            byte[] rawBytes;
            if (compression == 8)
            {
                rawBytes = new byte[expectedBytes];
                using var ms = new MemoryStream(fileBytes, offset, byteCount, writable: false);
                using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                zlib.ReadExactly(rawBytes);
            }
            else
            {
                rawBytes = fileBytes.AsSpan(offset, expectedBytes).ToArray();
            }

            int baseSample = firstRow * width;
            for (int i = 0; i < expectedSamples; i++)
            {
                dest[baseSample + i] = BinaryPrimitives.ReadSingleLittleEndian(rawBytes.AsSpan(i * 4, 4));
            }
        }

        string? manifestPath = StageArtifactPaths.TryFindCompanionManifest(fullPath);
        WorldManifest? manifest = ManifestWriter.TryRead(manifestPath);

        return new LoadedElevationMap(map, planetRadius, fullPath, manifest);
    }

    private static uint ReadScalarUInt(byte[] file, ushort type, uint count, ReadOnlySpan<byte> valueField)
    {
        uint[] values = ReadUIntArray(file, type, count, valueField);
        return values.Length > 0 ? values[0] : 0;
    }

    private static uint[] ReadUIntArray(byte[] file, ushort type, uint count, ReadOnlySpan<byte> valueField)
    {
        int elemSize = type switch
        {
            TypeShort => 2,
            TypeLong => 4,
            _ => throw new InvalidDataException($"Unexpected TIFF integer tag type {type}."),
        };

        long totalBytes = (long)count * elemSize;
        ReadOnlySpan<byte> source = totalBytes <= 4
            ? valueField[..(int)totalBytes]
            : file.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(valueField), (int)totalBytes);

        var result = new uint[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = type == TypeShort
                ? BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(i * 2, 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(i * 4, 4));
        }

        return result;
    }

    private static double[] ReadDoubleArray(byte[] file, ushort type, uint count, ReadOnlySpan<byte> valueField)
    {
        if (type != TypeDouble)
        {
            throw new InvalidDataException($"Expected DOUBLE tag type (12), got {type}.");
        }

        int totalBytes = (int)(count * 8);
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(valueField);
        ReadOnlySpan<byte> source = file.AsSpan(offset, totalBytes);

        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(i * 8, 8));
        }

        return result;
    }
}
