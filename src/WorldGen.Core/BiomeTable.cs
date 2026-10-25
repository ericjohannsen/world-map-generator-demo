using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WorldGen;

/// <summary>An 8-bit RGB colour used by biome palettes.</summary>
public readonly record struct BiomeColor(byte R, byte G, byte B)
{
    /// <summary>Marker colour for a cell that matched no rule; deliberately garish so a gap is obvious.</summary>
    public static readonly BiomeColor Unclassified = new(255, 0, 255);

    public static BiomeColor Parse(string hex)
    {
        string text = hex.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length == 3)
        {
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        }

        if (text.Length != 6 ||
            !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            throw new FormatException($"Invalid biome colour '{hex}'.");
        }

        return new BiomeColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The named climate/moisture dimensions a biome rule can test. Adding a dimension here (and populating it
/// in <see cref="TerrainBiomes"/>) lets a biome set use it without any change to the table structure.
/// </summary>
public static class BiomeIndices
{
    public const int TemperatureC = 0;
    public const int Moisture = 1;
    public const int ElevationM = 2;
    public const int Riparian = 3;
    public const int Coastal = 4;
    public const int Count = 5;

    private static readonly Dictionary<string, int> Populated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temperatureC"] = TemperatureC,
        ["moisture"] = Moisture,
        ["elevationM"] = ElevationM,
        ["riparian"] = Riparian,
        ["coastal"] = Coastal,
    };

    /// <summary>Dimensions understood by the schema but not yet produced (a future seasonal model).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "temperatureSeasonalityC",
        "precipitationSeasonality",
        "summerMoisture",
        "winterMoisture",
    };

    public static bool TryGetSlot(string name, out int slot) => Populated.TryGetValue(name, out slot);

    public static bool IsKnown(string name) => Populated.ContainsKey(name) || Reserved.Contains(name);
}

internal readonly struct BiomeCondition
{
    public BiomeCondition(int slot, float? min, float? max)
    {
        Slot = slot;
        Min = min;
        Max = max;
    }

    public int Slot { get; }

    public float? Min { get; }

    public float? Max { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(ReadOnlySpan<float> indices)
    {
        float value = indices[Slot];
        return (!Min.HasValue || value >= Min.Value) && (!Max.HasValue || value <= Max.Value);
    }
}

/// <summary>One land biome: an id, presentation metadata, and a priority-ordered set of index bounds.</summary>
public sealed class BiomeDefinition
{
    internal BiomeDefinition(
        int id,
        string key,
        string name,
        int priority,
        BiomeColor color,
        IReadOnlyList<string> conditions,
        BiomeCondition[] compiled,
        bool hadConditions)
    {
        Id = id;
        Key = key;
        Name = name;
        Priority = priority;
        Color = color;
        Conditions = conditions;
        Compiled = compiled;
        HadConditions = hadConditions;
    }

    public int Id { get; }

    public string Key { get; }

    public string Name { get; }

    public int Priority { get; }

    public BiomeColor Color { get; }

    /// <summary>The index names this rule tests, for diagnostics and the manifest.</summary>
    public IReadOnlyList<string> Conditions { get; }

    internal BiomeCondition[] Compiled { get; }

    /// <summary>True when the rule declared conditions (possibly all reserved, in which case it never matches).</summary>
    internal bool HadConditions { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(ReadOnlySpan<float> indices)
    {
        if (!HadConditions)
        {
            return true;
        }

        BiomeCondition[] compiled = Compiled;
        if (compiled.Length == 0)
        {
            // Every declared dimension is reserved/unpopulated: the rule stays dormant rather than becoming a catch-all.
            return false;
        }

        for (int i = 0; i < compiled.Length; i++)
        {
            if (!compiled[i].Matches(indices))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A reserved water class shared by every biome set (ocean, lake, river, sea ice).</summary>
public sealed record BiomeWaterClass(int Id, string Key, string Name, BiomeColor Color);

/// <summary>A named id → colour mapping. Palettes are presentation only and never affect the id grid.</summary>
public sealed class BiomePalette
{
    private readonly Dictionary<int, BiomeColor> _colors;

    internal BiomePalette(string id, string name, Dictionary<int, BiomeColor> colors)
    {
        Id = id;
        Name = name;
        _colors = colors;
    }

    public string Id { get; }

    public string Name { get; }

    public IReadOnlyDictionary<int, BiomeColor> Colors => _colors;

    public BiomeColor ColorOf(int id) => _colors.TryGetValue(id, out BiomeColor color) ? color : BiomeColor.Unclassified;
}

/// <summary>One named, versioned biome set: its land rules and its palettes.</summary>
public sealed class BiomeSet
{
    internal BiomeSet(
        string id,
        int version,
        string name,
        BiomeDefinition[] biomes,
        Dictionary<int, BiomeDefinition> byId,
        Dictionary<string, BiomePalette> palettes,
        string defaultPaletteId)
    {
        Id = id;
        Version = version;
        Name = name;
        Biomes = biomes;
        ById = byId;
        Palettes = palettes;
        DefaultPaletteId = defaultPaletteId;
    }

    public string Id { get; }

    public int Version { get; }

    public string Name { get; }

    /// <summary>Land rules sorted by descending priority, then file order.</summary>
    public IReadOnlyList<BiomeDefinition> Biomes { get; }

    public IReadOnlyDictionary<int, BiomeDefinition> ById { get; }

    public IReadOnlyDictionary<string, BiomePalette> Palettes { get; }

    public string DefaultPaletteId { get; }

    public BiomePalette Palette(string? id = null) =>
        id is not null && Palettes.TryGetValue(id, out BiomePalette? palette)
            ? palette
            : Palettes[DefaultPaletteId];
}

/// <summary>
/// Read-only view of the embedded <c>biomes.json</c>: the reserved water classes and every named biome set.
/// The table is the source of truth for classification rules and default colours; only <c>whittaker</c> ships
/// today, and adding a set is a data change (see ADR-021).
/// </summary>
public sealed class BiomeTable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, BiomeSet> _sets;

    private BiomeTable(
        int schemaVersion,
        string defaultSetId,
        IReadOnlyList<BiomeWaterClass> waterClasses,
        Dictionary<string, BiomeSet> sets)
    {
        SchemaVersion = schemaVersion;
        DefaultSetId = defaultSetId;
        WaterClasses = waterClasses;
        _sets = sets;
    }

    public static BiomeTable Default { get; } = LoadEmbedded();

    public int SchemaVersion { get; }

    public string DefaultSetId { get; }

    public IReadOnlyList<BiomeWaterClass> WaterClasses { get; }

    public IReadOnlyCollection<BiomeSet> Sets => _sets.Values;

    public BiomeSet GetSet(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && _sets.TryGetValue(id, out BiomeSet? set))
        {
            return set;
        }

        return _sets[DefaultSetId];
    }

    public bool TryGetSet(string? id, out BiomeSet set)
    {
        if (!string.IsNullOrWhiteSpace(id) && _sets.TryGetValue(id, out BiomeSet? found))
        {
            set = found;
            return true;
        }

        set = _sets[DefaultSetId];
        return false;
    }

    public static BiomeTable Parse(string json)
    {
        TableDto? dto = JsonSerializer.Deserialize<TableDto>(json, Options)
            ?? throw new InvalidOperationException("The biome table is empty.");

        if (dto.WaterClasses is null || dto.WaterClasses.Count == 0)
        {
            throw new InvalidOperationException("biomes.json defines no water classes.");
        }

        if (dto.Sets is null || dto.Sets.Count == 0)
        {
            throw new InvalidOperationException("biomes.json defines no biome sets.");
        }

        var water = new List<BiomeWaterClass>();
        foreach (WaterDto w in dto.WaterClasses)
        {
            water.Add(new BiomeWaterClass(
                w.Id,
                w.Key ?? $"water_{w.Id}",
                w.Name ?? w.Key ?? $"Water {w.Id}",
                BiomeColor.Parse(w.Color ?? "#000000")));
        }

        var waterIds = water.Select(w => w.Id).ToHashSet();
        var sets = new Dictionary<string, BiomeSet>(StringComparer.OrdinalIgnoreCase);
        foreach ((string setId, SetDto setDto) in dto.Sets)
        {
            var defaultColors = new Dictionary<int, BiomeColor>();
            foreach (BiomeWaterClass w in water)
            {
                defaultColors[w.Id] = w.Color;
            }

            var biomes = new List<BiomeDefinition>();
            foreach (BiomeDto b in setDto.Biomes ?? new List<BiomeDto>())
            {
                if (waterIds.Contains(b.Id))
                {
                    throw new InvalidOperationException($"Biome id {b.Id} in set '{setId}' collides with a reserved water id.");
                }

                var conditions = new List<BiomeCondition>();
                var names = new List<string>();
                bool hadConditions = b.When is { Count: > 0 };
                if (hadConditions)
                {
                    foreach ((string indexName, BoundDto bound) in b.When!)
                    {
                        if (!BiomeIndices.IsKnown(indexName))
                        {
                            throw new InvalidOperationException(
                                $"Biome '{(b.Key ?? b.Id.ToString(CultureInfo.InvariantCulture))}' in set '{setId}' " +
                                $"references unknown index '{indexName}'.");
                        }

                        if (BiomeIndices.TryGetSlot(indexName, out int slot))
                        {
                            conditions.Add(new BiomeCondition(slot, bound.Min, bound.Max));
                            names.Add(indexName);
                        }
                    }
                }

                var color = BiomeColor.Parse(b.Color ?? "#808080");
                defaultColors[b.Id] = color;
                biomes.Add(new BiomeDefinition(
                    b.Id,
                    b.Key ?? $"biome_{b.Id}",
                    b.Name ?? b.Key ?? $"Biome {b.Id}",
                    b.Priority,
                    color,
                    names,
                    conditions.ToArray(),
                    hadConditions));
            }

            if (biomes.Count == 0)
            {
                throw new InvalidOperationException($"Biome set '{setId}' defines no biomes.");
            }

            // OrderBy is stable, so equal priorities keep file order.
            BiomeDefinition[] ordered = biomes.OrderByDescending(x => x.Priority).ToArray();
            var byId = ordered.ToDictionary(x => x.Id);

            string defaultPaletteId = setDto.DefaultPalette ?? "natural";
            var palettes = new Dictionary<string, BiomePalette>(StringComparer.OrdinalIgnoreCase);
            if (setDto.Palettes is { Count: > 0 })
            {
                foreach ((string paletteId, PaletteDto palette) in setDto.Palettes)
                {
                    var colors = new Dictionary<int, BiomeColor>(defaultColors);
                    if (palette.Colors is not null)
                    {
                        foreach ((string rawId, string hex) in palette.Colors)
                        {
                            if (!int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                            {
                                throw new InvalidOperationException($"Palette '{paletteId}' has a non-numeric biome id '{rawId}'.");
                            }

                            colors[id] = BiomeColor.Parse(hex);
                        }
                    }

                    palettes[paletteId] = new BiomePalette(paletteId, palette.Name ?? paletteId, colors);
                }

                if (!palettes.ContainsKey(defaultPaletteId))
                {
                    defaultPaletteId = palettes.Keys.First();
                }
            }
            else
            {
                palettes[defaultPaletteId] = new BiomePalette(defaultPaletteId, "Natural", new Dictionary<int, BiomeColor>(defaultColors));
            }

            sets[setId] = new BiomeSet(setId, setDto.Version, setDto.Name ?? setId, ordered, byId, palettes, defaultPaletteId);
        }

        return new BiomeTable(dto.SchemaVersion, dto.DefaultSet ?? sets.Keys.First(), water, sets);
    }

    private static BiomeTable LoadEmbedded()
    {
        Assembly assembly = typeof(BiomeTable).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("biomes.json", StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidOperationException("The embedded biome table (biomes.json) is missing.");
        }

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private sealed class TableDto
    {
        public int SchemaVersion { get; set; }

        public string? DefaultSet { get; set; }

        public List<WaterDto>? WaterClasses { get; set; }

        public Dictionary<string, SetDto>? Sets { get; set; }
    }

    private sealed class WaterDto
    {
        public int Id { get; set; }

        public string? Key { get; set; }

        public string? Name { get; set; }

        public string? Color { get; set; }
    }

    private sealed class SetDto
    {
        public int Version { get; set; }

        public string? Name { get; set; }

        public string? DefaultPalette { get; set; }

        public List<BiomeDto>? Biomes { get; set; }

        public Dictionary<string, PaletteDto>? Palettes { get; set; }
    }

    private sealed class BiomeDto
    {
        public int Id { get; set; }

        public string? Key { get; set; }

        public string? Name { get; set; }

        public int Priority { get; set; }

        public Dictionary<string, BoundDto>? When { get; set; }

        public string? Color { get; set; }
    }

    private sealed class BoundDto
    {
        public float? Min { get; set; }

        public float? Max { get; set; }
    }

    private sealed class PaletteDto
    {
        public string? Name { get; set; }

        public Dictionary<string, string>? Colors { get; set; }
    }
}
