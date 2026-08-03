namespace DrawAim.Infrastructure.Storage;

/// <summary>Resolves every persistent path below one application-owned root.</summary>
public sealed class DrawAimDataPaths
{
    public const string DataRootEnvironmentVariable = "DRAWAIM_DATA_ROOT";

    public DrawAimDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootDirectory));
    }

    public string RootDirectory { get; }

    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public string HistoryFilePath => Path.Combine(RootDirectory, "history.json");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string RecoveryDirectory => Path.Combine(RootDirectory, "recovery");

    public static DrawAimDataPaths Resolve()
    {
        string? redirectedRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(redirectedRoot))
        {
            return new DrawAimDataPaths(redirectedRoot);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The LocalApplicationData directory is unavailable.");
        }

        return new DrawAimDataPaths(Path.Combine(localAppData, "DrawAim"));
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }
}
