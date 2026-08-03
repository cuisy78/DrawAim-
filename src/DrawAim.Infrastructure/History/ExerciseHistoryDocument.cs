namespace DrawAim.Infrastructure.History;

public sealed class ExerciseHistoryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<ExerciseHistoryEntry> Entries { get; set; } = [];
}

public sealed class HistoryStoreOptions
{
    public const int DefaultMaxEntries = 10_000;

    public int MaxEntries { get; init; } = DefaultMaxEntries;

    internal int GetValidatedMaxEntries()
    {
        if (MaxEntries is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEntries),
                MaxEntries,
                "MaxEntries must be between 1 and 1,000,000.");
        }

        return MaxEntries;
    }
}
