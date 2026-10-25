using System;
using System.IO;

namespace WorldGen.App.Settings;

/// <summary>Per-user locations for settings, cache, and logs.</summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string PreferencesFile => Path.Combine(Root, "preferences.json");

    public static string SessionFile => Path.Combine(Root, "session.json");

    public static string LegacySettingsFile => Path.Combine(Root, "ui-settings.json");

    public static string CacheDirectory => Path.Combine(Root, "cache");

    public static string LogDirectory => Path.Combine(Root, "logs");

    private static string ResolveRoot()
    {
        // .NET's LocalApplicationData is XDG on Unix but not the Apple convention on macOS.
        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "WorldGen");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorldGen");
    }
}
