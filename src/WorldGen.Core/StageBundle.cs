using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WorldGen;

/// <summary>
/// Self-describing binary bundle for one cached stage result: metadata plus named float/int/byte arrays,
/// compressed with Deflate. The bundle is deliberately independent of the public GeoTIFF/PNG exports.
/// </summary>
public sealed class StageBundle
{
    private static readonly byte[] Magic = "WGC1"u8.ToArray();
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Stage { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string InputKey { get; set; } = string.Empty;
    public int ModelVersion { get; set; }
    public int SettingsSchemaVersion { get; set; } = ModelVersions.SettingsSchema;
    public int CacheSchemaVersion { get; set; } = ModelVersions.CacheSchema;
    public string SettingsJson { get; set; } = string.Empty;
    public string? MetricsJson { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public MapView View { get; set; }

    public Dictionary<string, float[]> FloatArrays { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int[]> IntArrays { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> ByteArrays { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, sbyte[]> SByteArrays { get; } = new(StringComparer.Ordinal);

    public void Save(string path)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Stage bundles assume a little-endian runtime.");
        }

        var arrays = new List<BundleArrayInfo>();
        arrays.AddRange(FloatArrays.Select(pair => new BundleArrayInfo(pair.Key, "float32", pair.Value.Length)));
        arrays.AddRange(IntArrays.Select(pair => new BundleArrayInfo(pair.Key, "int32", pair.Value.Length)));
        arrays.AddRange(ByteArrays.Select(pair => new BundleArrayInfo(pair.Key, "byte", pair.Value.Length)));
        arrays.AddRange(SByteArrays.Select(pair => new BundleArrayInfo(pair.Key, "sbyte", pair.Value.Length)));

        var header = new BundleHeader
        {
            FormatVersion = FormatVersion,
            Stage = Stage,
            Key = Key,
            InputKey = InputKey,
            ModelVersion = ModelVersion,
            SettingsSchemaVersion = SettingsSchemaVersion,
            CacheSchemaVersion = CacheSchemaVersion,
            SettingsJson = SettingsJson,
            MetricsJson = MetricsJson,
            Width = Width,
            Height = Height,
            View = new BundleView
            {
                North = View.NorthLatitude,
                South = View.SouthLatitude,
                West = View.WestLongitude,
                East = View.EastLongitude,
            },
            Arrays = arrays,
        };

        byte[] headerJson = JsonSerializer.SerializeToUtf8Bytes(header, Options);

        using FileStream file = File.Create(path);
        file.Write(Magic);

        using var deflate = new DeflateStream(file, CompressionLevel.Optimal, leaveOpen: false);
        using var writer = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: false);
        writer.Write(headerJson.Length);
        writer.Write(headerJson);

        foreach (BundleArrayInfo info in arrays)
        {
            WriteArray(writer, info);
        }

        writer.Flush();
    }

    public static StageBundle Load(string path)
    {
        using FileStream file = File.OpenRead(path);

        Span<byte> magic = stackalloc byte[4];
        file.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a WorldGen stage bundle.");
        }

        using var deflate = new DeflateStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new BinaryReader(deflate, Encoding.UTF8, leaveOpen: false);

        int headerLength = reader.ReadInt32();
        if (headerLength <= 0 || headerLength > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid stage bundle header length.");
        }

        byte[] headerJson = reader.ReadBytes(headerLength);
        if (headerJson.Length != headerLength)
        {
            throw new InvalidDataException("Truncated stage bundle header.");
        }

        BundleHeader header = JsonSerializer.Deserialize<BundleHeader>(headerJson, Options)
            ?? throw new InvalidDataException("Invalid stage bundle header.");

        if (header.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported stage bundle format {header.FormatVersion}.");
        }

        if (header.CacheSchemaVersion != ModelVersions.CacheSchema)
        {
            throw new InvalidDataException(
                $"Cached stage uses cache schema {header.CacheSchemaVersion}; expected {ModelVersions.CacheSchema}.");
        }

        MapView view = new(header.View.North, header.View.South, header.View.West, header.View.East);
        view.Validate();

        var bundle = new StageBundle
        {
            Stage = header.Stage,
            Key = header.Key,
            InputKey = header.InputKey,
            ModelVersion = header.ModelVersion,
            SettingsSchemaVersion = header.SettingsSchemaVersion,
            CacheSchemaVersion = header.CacheSchemaVersion,
            SettingsJson = header.SettingsJson,
            MetricsJson = header.MetricsJson,
            Width = header.Width,
            Height = header.Height,
            View = view,
        };

        foreach (BundleArrayInfo info in header.Arrays)
        {
            ReadArray(reader, bundle, info);
        }

        return bundle;
    }

    private void WriteArray(BinaryWriter writer, BundleArrayInfo info)
    {
        switch (info.Type)
        {
            case "float32":
                WriteFloats(writer, FloatArrays[info.Name]);
                break;
            case "int32":
                WriteInts(writer, IntArrays[info.Name]);
                break;
            case "byte":
                writer.Write(ByteArrays[info.Name]);
                break;
            case "sbyte":
                WriteSBytes(writer, SByteArrays[info.Name]);
                break;
            default:
                throw new InvalidDataException($"Unknown bundle array type '{info.Type}'.");
        }
    }

    private static void ReadArray(BinaryReader reader, StageBundle bundle, BundleArrayInfo info)
    {
        int count = info.Count;
        if (count < 0)
        {
            throw new InvalidDataException("Negative array count in stage bundle.");
        }

        switch (info.Type)
        {
            case "float32":
            {
                byte[] bytes = ReadExactly(reader, count * sizeof(float));
                var values = new float[count];
                Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
                bundle.FloatArrays[info.Name] = values;
                break;
            }
            case "int32":
            {
                byte[] bytes = ReadExactly(reader, count * sizeof(int));
                var values = new int[count];
                Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
                bundle.IntArrays[info.Name] = values;
                break;
            }
            case "byte":
                bundle.ByteArrays[info.Name] = ReadExactly(reader, count);
                break;
            case "sbyte":
            {
                byte[] bytes = ReadExactly(reader, count);
                var values = new sbyte[count];
                Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
                bundle.SByteArrays[info.Name] = values;
                break;
            }
            default:
                throw new InvalidDataException($"Unknown bundle array type '{info.Type}'.");
        }
    }

    private static void WriteFloats(BinaryWriter writer, float[] values)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
        writer.Write(bytes);
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
        writer.Write(bytes);
    }

    private static void WriteSBytes(BinaryWriter writer, sbyte[] values)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
        writer.Write(bytes);
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new InvalidDataException("Truncated stage bundle payload.");
        }

        return bytes;
    }

    private sealed class BundleHeader
    {
        public int FormatVersion { get; set; }
        public string Stage { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string InputKey { get; set; } = string.Empty;
        public int ModelVersion { get; set; }
        public int SettingsSchemaVersion { get; set; }
        public int CacheSchemaVersion { get; set; }
        public string SettingsJson { get; set; } = string.Empty;
        public string? MetricsJson { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public BundleView View { get; set; } = new();
        public List<BundleArrayInfo> Arrays { get; set; } = new();
    }

    private sealed class BundleView
    {
        public double North { get; set; }
        public double South { get; set; }
        public double West { get; set; }
        public double East { get; set; }
    }

    private sealed record BundleArrayInfo(string Name, string Type, int Count);
}
