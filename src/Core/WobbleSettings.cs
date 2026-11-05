using System.Text.Json;

namespace WobblyWindows.Core;

internal sealed class WobbleSettings
{
    public bool Enabled { get; set; } = true;
    public float Strength { get; set; } = 1;
    public float Frequency { get; set; } = 3.2f;
    public float Damping { get; set; } = 0.48f;
    public HashSet<uint> ExcludedWindows { get; set; } = [];

    public void Normalize()
    {
        Strength = Clamp(Strength, 0, 2, 1);
        Frequency = Clamp(Frequency, 2, 6, 3.2f);
        Damping = Clamp(Damping, 0.25f, 1, 0.48f);
        ExcludedWindows ??= [];
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public static WobbleSettings Load(string path)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<WobbleSettings>(File.ReadAllText(path)) ?? new() : new();
        settings.Normalize();
        return settings;
    }

    public void Save(string path)
    {
        Normalize();
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
