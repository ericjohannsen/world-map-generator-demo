using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldGen;

/// <summary>One cached stage result on disk.</summary>
public sealed class CacheEntry
{
    public string Stage { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string InputKey { get; set; } = string.Empty;
    public int ModelVersion { get; set; }
    public int SettingsSchemaVersion { get; set; }
    public int CacheSchemaVersion { get; set; }
    public string SettingsSummary { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
    public string LastUsedUtc { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string[] Arrays { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public string DirectoryPath { get; set; } = string.Empty;

    [JsonIgnore]
    public string BundlePath => Path.Combine(DirectoryPath, "data.wgc");

    [JsonIgnore]
    public string EntryPath => Path.Combine(DirectoryPath, "stage.json");

    [JsonIgnore]
    public ModelStatus Status => ModelRegistry.Default.StatusOf(Stage, ModelVersion);
}

/// <summary>
/// Content-addressed stage cache under a per-user directory. Each entry is a stage key directory containing
/// a self-describing <c>stage.json</c>, a <c>data.wgc</c> bundle, and nothing else. The index is derived and
/// can be rebuilt by scanning the stage directories.
/// </summary>
public sealed class StageCache
{
    public const int IndexSchemaVersion = 1;
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<CacheEntry> _entries = new();

    public StageCache(string? root = null)
    {
        Root = root ?? DefaultRoot;
        Directory.CreateDirectory(Root);
        LoadOrRebuildIndex();
    }

    public static string DefaultRoot
    {
        get
        {
            string baseDirectory = OperatingSystem.IsMacOS()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(baseDirectory, "WorldGen", "cache");
        }
    }

    public string Root { get; }

    public IReadOnlyList<CacheEntry> Entries => _entries;

    public long TotalSizeBytes => _entries.Sum(entry => entry.SizeBytes);

    public bool TryGet(string stage, string key, out CacheEntry entry)
    {
        CacheEntry? found = _entries.FirstOrDefault(
            candidate => string.Equals(candidate.Stage, stage, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

        if (found is null)
        {
            // The index is derived data. Recover a stage that exists on disk even if a stale or empty
            // index missed it (for example after a crash between the bundle write and the index write).
            string stageDirectory = Path.Combine(Root, stage.ToLowerInvariant(), key);
            string entryPath = Path.Combine(stageDirectory, "stage.json");
            string dataPath = Path.Combine(stageDirectory, "data.wgc");
            if (File.Exists(entryPath) && File.Exists(dataPath))
            {
                try
                {
                    CacheEntry? recovered = JsonSerializer.Deserialize<CacheEntry>(
                        File.ReadAllText(entryPath), Options);
                    if (recovered is not null)
                    {
                        recovered.DirectoryPath = stageDirectory;
                        recovered.SizeBytes = new FileInfo(dataPath).Length + new FileInfo(entryPath).Length;
                        _entries.Add(recovered);
                        SaveIndex();
                        found = recovered;
                    }
                }
                catch
                {
                    // Treat an unreadable sidecar as a miss.
                }
            }
        }

        if (found is null)
        {
            entry = null!;
            return false;
        }

        entry = found;
        if (!File.Exists(entry.BundlePath))
        {
            _entries.Remove(entry);
            SaveIndex();
            return false;
        }

        return true;
    }

    public StageBundle Load(CacheEntry entry)
    {
        StageBundle bundle = StageBundle.Load(entry.BundlePath);
        Touch(entry);
        return bundle;
    }

    public void Store(StageBundle bundle, string settingsSummary)
    {
        if (bundle.CacheSchemaVersion != ModelVersions.CacheSchema)
        {
            throw new InvalidOperationException(
                $"Stage bundle cache schema {bundle.CacheSchemaVersion} does not match {ModelVersions.CacheSchema}.");
        }

        string stageDirectory = Path.Combine(Root, bundle.Stage.ToLowerInvariant(), bundle.Key);
        Directory.CreateDirectory(stageDirectory);

        string dataPath = Path.Combine(stageDirectory, "data.wgc");
        string dataTemp = dataPath + ".tmp";
        bundle.Save(dataTemp);
        File.Move(dataTemp, dataPath, overwrite: true);

        var entry = new CacheEntry
        {
            Stage = bundle.Stage.ToLowerInvariant(),
            Key = bundle.Key,
            InputKey = bundle.InputKey,
            ModelVersion = bundle.ModelVersion,
            SettingsSchemaVersion = bundle.SettingsSchemaVersion,
            CacheSchemaVersion = bundle.CacheSchemaVersion,
            SettingsSummary = settingsSummary,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            LastUsedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Arrays = bundle.FloatArrays.Keys
                .Concat(bundle.IntArrays.Keys)
                .Concat(bundle.ByteArrays.Keys)
                .Concat(bundle.SByteArrays.Keys)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            DirectoryPath = stageDirectory,
        };

        WriteEntry(stageDirectory, entry);
        entry.SizeBytes = new FileInfo(dataPath).Length + new FileInfo(entry.EntryPath).Length;

        _entries.RemoveAll(candidate =>
            string.Equals(candidate.Stage, entry.Stage, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
        _entries.Add(entry);
        SaveIndex();
    }

    public void Touch(CacheEntry entry)
    {
        entry.LastUsedUtc = DateTimeOffset.UtcNow.ToString("O");
        WriteEntry(entry.DirectoryPath, entry);
        SaveIndex();
    }

    public int Prune(Func<CacheEntry, bool> predicate, bool dryRun)
    {
        CacheEntry[] victims = _entries.Where(predicate).ToArray();
        if (dryRun)
        {
            return victims.Length;
        }

        int removed = 0;
        foreach (CacheEntry entry in victims)
        {
            bool deleted = true;
            try
            {
                if (Directory.Exists(entry.DirectoryPath))
                {
                    Directory.Delete(entry.DirectoryPath, recursive: true);
                }
            }
            catch
            {
                // A locked or already-removed directory is not worth failing the whole prune.
                deleted = false;
            }

            if (deleted)
            {
                _entries.Remove(entry);
                removed++;
            }
        }

        if (removed > 0)
        {
            SaveIndex();
        }

        return removed;
    }

    public void RebuildIndex()
    {
        _entries.Clear();

        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (string stageDirectory in Directory.GetDirectories(Root))
        {
            string stageName = Path.GetFileName(stageDirectory);
            if (!stageName.StartsWith('s'))
            {
                continue;
            }

            foreach (string keyDirectory in Directory.GetDirectories(stageDirectory))
            {
                string entryPath = Path.Combine(keyDirectory, "stage.json");
                string dataPath = Path.Combine(keyDirectory, "data.wgc");
                if (!File.Exists(entryPath) || !File.Exists(dataPath))
                {
                    continue;
                }

                try
                {
                    CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(
                        File.ReadAllText(entryPath), Options);
                    if (entry is null)
                    {
                        continue;
                    }

                    entry.DirectoryPath = keyDirectory;
                    entry.SizeBytes = new FileInfo(dataPath).Length + new FileInfo(entryPath).Length;
                    _entries.Add(entry);
                }
                catch
                {
                    // Ignore a malformed entry; cleanup can remove it later.
                }
            }
        }

        _entries.Sort((a, b) => string.Compare(a.Stage, b.Stage, StringComparison.Ordinal));
        SaveIndex();
    }

    private void LoadOrRebuildIndex()
    {
        string indexPath = Path.Combine(Root, IndexFileName);
        if (!File.Exists(indexPath))
        {
            RebuildIndex();
            return;
        }

        try
        {
            CacheIndex? index = JsonSerializer.Deserialize<CacheIndex>(
                File.ReadAllText(indexPath), Options);
            if (index is null || index.SchemaVersion != IndexSchemaVersion)
            {
                RebuildIndex();
                return;
            }

            foreach (CacheEntry entry in index.Entries)
            {
                entry.DirectoryPath = Path.Combine(Root, entry.Stage, entry.Key);
                if (File.Exists(entry.BundlePath))
                {
                    _entries.Add(entry);
                }
            }

            if (_entries.Count == 0 && Directory.EnumerateDirectories(Root, "s*").Any())
            {
                // An empty/stale index must not hide stage directories that are actually present.
                RebuildIndex();
            }
        }
        catch
        {
            TryQuarantine(indexPath);
            RebuildIndex();
        }
    }

    private void SaveIndex()
    {
        string indexPath = Path.Combine(Root, IndexFileName);
        string temporary = indexPath + ".tmp";

        var index = new CacheIndex
        {
            SchemaVersion = IndexSchemaVersion,
            Entries = _entries
                .OrderBy(entry => entry.Stage, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList(),
        };

        File.WriteAllText(temporary, JsonSerializer.Serialize(index, Options));

        if (File.Exists(indexPath))
        {
            File.Move(temporary, indexPath, overwrite: true);
        }
        else
        {
            File.Move(temporary, indexPath);
        }
    }

    private static void WriteEntry(string stageDirectory, CacheEntry entry)
    {
        string path = Path.Combine(stageDirectory, "stage.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entry, Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            string directory = Path.GetDirectoryName(path) ?? ".";
            string quarantine = Path.Combine(
                directory,
                $"index.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Move(path, quarantine, overwrite: true);
        }
        catch
        {
            // Best effort; a rebuild will overwrite the index anyway.
        }
    }

    private sealed class CacheIndex
    {
        public int SchemaVersion { get; set; }
        public List<CacheEntry> Entries { get; set; } = new();
    }
}
