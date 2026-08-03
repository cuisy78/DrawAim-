namespace DrawAim.Infrastructure.Settings;

/// <summary>Persisted application settings.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Theme { get; set; } = "Dark";

    public string Culture { get; set; } = "zh-CN";

    public bool HasCompletedFirstRunGuide { get; set; }

    public double BrushSize { get; set; } = 8.0;

    public ModeSettings ModeOne { get; set; } = new();

    public ModeSettings ModeTwo { get; set; } = new();

    public ModeSettings ModeThree { get; set; } = new();

    public static AppSettings CreateDefault() => new();

    internal static AppSettings Normalize(AppSettings? value)
    {
        AppSettings source = value ?? CreateDefault();

        return new AppSettings
        {
            SchemaVersion = source.SchemaVersion <= 0
                ? CurrentSchemaVersion
                : source.SchemaVersion,
            Theme = NormalizeTheme(source.Theme),
            Culture = string.IsNullOrWhiteSpace(source.Culture) ? "zh-CN" : source.Culture.Trim(),
            HasCompletedFirstRunGuide = source.HasCompletedFirstRunGuide,
            BrushSize = double.IsFinite(source.BrushSize)
                ? Math.Clamp(source.BrushSize, 0.5, 256.0)
                : 8.0,
            ModeOne = ModeSettings.Normalize(source.ModeOne),
            ModeTwo = ModeSettings.Normalize(source.ModeTwo),
            ModeThree = ModeSettings.Normalize(source.ModeThree),
        };
    }

    private static string NormalizeTheme(string? theme) =>
        string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
}
