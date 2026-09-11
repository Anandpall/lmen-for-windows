using System;
using System.IO;
using System.Text.Json;
using LumnWindows.Helpers;

namespace LumnWindows.Settings;

public class SettingsService
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LumnWindows");
    
    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        Current = Load();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    Current = loaded;
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to default on parse error
        }

        Current = new AppSettings();
        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        try
        {
            if (!Directory.Exists(SettingsFolder))
            {
                Directory.CreateDirectory(SettingsFolder);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFile, json);
            Win32Helper.SetStartWithWindows(settings.StartWithWindows);
            SettingsChanged?.Invoke(settings);
        }
        catch
        {
            // Handle IO errors gracefully
        }
    }
}
