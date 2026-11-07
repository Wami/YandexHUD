using System.Text.Json;
using Android.Content;

namespace MockRoute;

internal sealed record RoutePoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Speed { get; set; }
    public double Bearing { get; set; }
}

internal sealed class RouteFile
{
    public string? Name { get; set; }
    public List<RoutePoint> Points { get; set; } = [];
}

internal static class RouteStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static (RouteFile? Route, string Source) Load(Context context)
    {
        string? external = context.GetExternalFilesDir(null)?.AbsolutePath;
        string? internalDir = context.FilesDir?.AbsolutePath;
        foreach (var (path, source) in new[]
        {
            (external is null ? null : Path.Combine(external, "route.json"), "external"),
            (internalDir is null ? null : Path.Combine(internalDir, "route.json"), "internal"),
        })
        {
            if (path is not null && File.Exists(path))
                return (Parse(File.ReadAllText(path)), source);
        }
        try
        {
            using var stream = context.Assets?.Open("route.json");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return (Parse(reader.ReadToEnd()), "assets");
            }
        }
        catch (Java.IO.FileNotFoundException) { }
        return (null, "нет");
    }

    private static RouteFile? Parse(string json)
    {
        var route = JsonSerializer.Deserialize<RouteFile>(json, Json);
        return route?.Points is { Count: >= 2 } ? route : null;
    }
}
