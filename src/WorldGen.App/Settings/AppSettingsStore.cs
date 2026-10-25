using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;

namespace WorldGen.App.Settings;

/// <summary>
/// Owns the two settings models and coalesces writes. All access is on the UI thread; stage generation never
/// touches settings.
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _root;
    private readonly DispatcherTimer _saveTimer;
    private readonly List<string> _warnings;

    public AppSettingsStore(string? root = null)
    {
        _root = root ?? AppPaths.Root;
        SettingsLoadResult result = SettingsFileStore.Load(_root);

        Preferences = result.Preferences;
        Session = result.Session;
        _warnings = result.Warnings.ToList();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();

        // ST-14: first run writes defaults so the files are discoverable.
        if (!File.Exists(Path.Combine(_root, "preferences.json")) &&
            !File.Exists(Path.Combine(_root, "session.json")))
        {
            SaveNow();
        }
    }

    public AppPreferences Preferences { get; }

    public AppSession Session { get; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Schedule a write after a short idle period; repeated calls collapse into one write.</summary>
    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Flush immediately; used on close and after explicit actions.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        SettingsFileStore.Save(_root, Preferences, Session, _warnings);
    }
}
