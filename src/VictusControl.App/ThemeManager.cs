using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace VictusControl.App;

public enum AppTheme { Dark, Light }

/// <summary>
/// Swaps the palette dictionary at runtime. Every component references palette
/// values with DynamicResource, so replacing the dictionary restyles the live
/// window without rebuilding it.
/// </summary>
public static class ThemeManager {

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VictusControl", "theme.txt");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme) {
        Current = theme;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d =>
            d.Source != null && (d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)
                              || d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)));

        var replacement = new ResourceDictionary {
            Source = new Uri(theme == AppTheme.Dark
                ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative)
        };

        if (existing != null) {
            // Insert at the old position so the palette still sits beneath Controls
            int index = dictionaries.IndexOf(existing);
            dictionaries.Insert(index, replacement);
            dictionaries.Remove(existing);
        } else {
            dictionaries.Insert(0, replacement);
        }

        Save(theme);
    }

    public static AppTheme LoadPreference() {
        try {
            if (File.Exists(SettingsPath) &&
                File.ReadAllText(SettingsPath).Trim()
                    .Equals("light", StringComparison.OrdinalIgnoreCase))
                return AppTheme.Light;
        } catch {
            // an unreadable preference just means the default
        }
        return AppTheme.Dark;
    }

    private static void Save(AppTheme theme) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, theme == AppTheme.Light ? "light" : "dark");
        } catch {
            // not worth surfacing; the theme still applied
        }
    }
}
