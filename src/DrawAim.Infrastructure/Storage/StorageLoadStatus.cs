namespace DrawAim.Infrastructure.Storage;

public enum StorageLoadStatus
{
    Loaded = 1,
    MissingUsedDefaults = 2,
    CorruptRecovered = 3,
    UnavailableUsedDefaults = 4,
}
