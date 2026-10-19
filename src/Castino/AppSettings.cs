using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Castino;

/// <summary>User preferences, stored as JSON in %LOCALAPPDATA%\Castino (works packaged and unpackaged).</summary>
public sealed class AppSettings
{
    public int MaxSize { get; set; } = 1920;
    public int MaxFps { get; set; } = 60;
    public int BitRateMbps { get; set; } = 8;
    public bool TurnScreenOff { get; set; }
    public bool SmoothScroll { get; set; } = true;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    /// <summary>0 = lenta, 1 = normale, 2 = veloce.</summary>
    public int ScrollSpeed { get; set; } = 1;
    public List<string> RecentAddresses { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Castino", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }

    public void RememberAddress(string address)
    {
        RecentAddresses.Remove(address);
        RecentAddresses.Insert(0, address);
        if (RecentAddresses.Count > 8)
            RecentAddresses.RemoveRange(8, RecentAddresses.Count - 8);
    }
}
