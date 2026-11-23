using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fjord.Core.Storage;

/// <summary>Where Fjord keeps its data: %APPDATA%\Fjord (roams with the Windows profile).</summary>
public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fjord");

    /// <summary>Machine-local cache (WebView2 profile etc.).</summary>
    public static string LocalDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fjord");

    public static string File(string name)
    {
        Directory.CreateDirectory(DataDirectory);
        return Path.Combine(DataDirectory, name);
    }
}

/// <summary>Tiny JSON file persistence with atomic writes (write temp, then replace).</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (!System.IO.File.Exists(path)) return new T();
            return JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path), Options) ?? new T();
        }
        catch (JsonException)
        {
            // Keep the broken file for manual rescue instead of silently overwriting it.
            System.IO.File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}
