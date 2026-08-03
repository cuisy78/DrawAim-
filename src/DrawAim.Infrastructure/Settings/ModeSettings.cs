using System.Text.Json;

namespace DrawAim.Infrastructure.Settings;

/// <summary>
/// Settings shared by a training mode. Mode-specific values can be stored in
/// <see cref="Options"/> without coupling persistence to the UI layer.
/// </summary>
public sealed class ModeSettings
{
    public string Difficulty { get; set; } = "Normal";

    public ulong Seed { get; set; }

    public string GeneratorVersion { get; set; } = "GeneratorV1";

    public int StrokeStabilization { get; set; }

    public string StabilizerVersion { get; set; } = "StrokeStabilizerV1";

    public Dictionary<string, double> LineTypeWeights { get; set; } = CreateDefaultWeights();

    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static ModeSettings Normalize(ModeSettings? value)
    {
        ModeSettings source = value ?? new ModeSettings();

        return new ModeSettings
        {
            Difficulty = NormalizeText(source.Difficulty, "Normal"),
            Seed = source.Seed,
            GeneratorVersion = NormalizeText(source.GeneratorVersion, "GeneratorV1"),
            StrokeStabilization = Math.Clamp(source.StrokeStabilization, 0, 100),
            StabilizerVersion = NormalizeText(source.StabilizerVersion, "StrokeStabilizerV1"),
            LineTypeWeights = NormalizeWeights(source.LineTypeWeights),
            Options = NormalizeOptions(source.Options),
        };
    }

    private static Dictionary<string, double> CreateDefaultWeights() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Straight"] = 1.0,
            ["C"] = 1.0,
            ["S"] = 1.0,
        };

    private static Dictionary<string, double> NormalizeWeights(
        Dictionary<string, double>? source)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (source is not null)
        {
            foreach ((string key, double weight) in source)
            {
                if (!string.IsNullOrWhiteSpace(key) && double.IsFinite(weight) && weight >= 0)
                {
                    result[key.Trim()] = weight;
                }
            }
        }

        return result.Count == 0 ? CreateDefaultWeights() : result;
    }

    private static Dictionary<string, JsonElement> NormalizeOptions(
        Dictionary<string, JsonElement>? source)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (source is null)
        {
            return result;
        }

        foreach ((string key, JsonElement option) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && option.ValueKind != JsonValueKind.Undefined)
            {
                result[key.Trim()] = option.Clone();
            }
        }

        return result;
    }

    private static string NormalizeText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
