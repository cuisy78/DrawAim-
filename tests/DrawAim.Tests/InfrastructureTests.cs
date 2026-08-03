using System.Text;
using System.Text.Json;
using DrawAim.Infrastructure.History;
using DrawAim.Infrastructure.Logging;
using DrawAim.Infrastructure.Settings;
using DrawAim.Infrastructure.Storage;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> InfrastructureTests()
    {
        yield return new TestCase(
            "Infrastructure/Signed component scores round-trip",
            SignedComponentScoresRoundTrip);
        yield return new TestCase("Infrastructure/设置默认值与原子保存读取", SettingsDefaultsAndRoundTrip);
        yield return new TestCase("Infrastructure/损坏设置备份并恢复", CorruptSettingsAreBackedUpAndRecovered);
        yield return new TestCase("Infrastructure/未知字段忽略且缺失字段补默认", UnknownAndMissingSettingsFields);
        yield return new TestCase("Infrastructure/历史达到上限后淘汰最旧记录", HistoryEvictsOldestEntries);
        yield return new TestCase("Infrastructure/损坏历史备份恢复后可继续追加", CorruptHistoryIsBackedUpAndRecovered);
        yield return new TestCase("Infrastructure/有限滚动日志", RollingLogIsBounded);
        yield return new TestCase("Infrastructure/DRAWAIM_DATA_ROOT 重定向", DataRootEnvironmentVariableRedirectsAllData);
    }

    private static void SettingsDefaultsAndRoundTrip()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        var store = new JsonSettingsStore(paths);

        SettingsLoadResult missing = store.LoadAsync().GetAwaiter().GetResult();
        AssertEx.Equal(StorageLoadStatus.MissingUsedDefaults, missing.Status);
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, missing.Settings.SchemaVersion);
        AssertEx.Equal("Dark", missing.Settings.Theme);
        AssertEx.Equal("zh-CN", missing.Settings.Culture);
        AssertEx.Equal(0, missing.Settings.ModeOne.StrokeStabilization);

        AppSettings settings = missing.Settings;
        settings.Theme = "Light";
        settings.HasCompletedFirstRunGuide = true;
        settings.BrushSize = 23.5;
        settings.ModeOne.Seed = 0x1234_5678UL;
        settings.ModeOne.StrokeStabilization = 73;
        settings.ModeOne.LineTypeWeights["Straight"] = 4.0;
        settings.ModeOne.Options["showStartPoint"] = JsonSerializer.SerializeToElement(false);

        store.SaveAsync(settings).GetAwaiter().GetResult();
        SettingsLoadResult loaded = store.LoadAsync().GetAwaiter().GetResult();

        AssertEx.Equal(StorageLoadStatus.Loaded, loaded.Status);
        AssertEx.Equal("Light", loaded.Settings.Theme);
        AssertEx.True(loaded.Settings.HasCompletedFirstRunGuide, "首次引导状态没有保存。");
        AssertEx.Near(23.5, loaded.Settings.BrushSize, 0.0);
        AssertEx.Equal(0x1234_5678UL, loaded.Settings.ModeOne.Seed);
        AssertEx.Equal(73, loaded.Settings.ModeOne.StrokeStabilization);
        AssertEx.Near(4.0, loaded.Settings.ModeOne.LineTypeWeights["Straight"], 0.0);
        AssertEx.False(
            loaded.Settings.ModeOne.Options["showStartPoint"].GetBoolean(),
            "模式专属 JSON 选项没有保存。" );
        AssertEx.True(File.Exists(paths.SettingsFilePath), "settings.json 没有创建。");
        AssertNoTemporaryFiles(directory.RootPath);
    }

    private static void CorruptSettingsAreBackedUpAndRecovered()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        Directory.CreateDirectory(paths.RootDirectory);
        const string corruptJson = "{ this is not valid json";
        File.WriteAllText(paths.SettingsFilePath, corruptJson, new UTF8Encoding(false));

        var store = new JsonSettingsStore(paths);
        SettingsLoadResult recovered = store.LoadAsync().GetAwaiter().GetResult();

        AssertEx.Equal(StorageLoadStatus.CorruptRecovered, recovered.Status);
        string backupPath = recovered.RecoveryBackupPath
            ?? throw new InvalidOperationException("恢复结果未报告损坏文件备份路径。");
        AssertStrictDescendant(paths.RecoveryDirectory, backupPath);
        AssertEx.True(File.Exists(backupPath), "损坏 settings.json 没有备份。");
        AssertEx.Equal(corruptJson, File.ReadAllText(backupPath, Encoding.UTF8));
        AssertEx.True(File.Exists(paths.SettingsFilePath), "恢复后的默认 settings.json 没有写回。");

        SettingsLoadResult secondLoad = store.LoadAsync().GetAwaiter().GetResult();
        AssertEx.Equal(StorageLoadStatus.Loaded, secondLoad.Status);
        AssertEx.Equal("Dark", secondLoad.Settings.Theme);
        AssertNoTemporaryFiles(directory.RootPath);
    }

    private static void UnknownAndMissingSettingsFields()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        Directory.CreateDirectory(paths.RootDirectory);

        const string forwardCompatibleJson = """
            {
              "theme": "Light",
              "futureRootField": { "enabled": true },
              "modeOne": {
                "seed": 42,
                "futureModeField": "ignored"
              }
            }
            """;
        File.WriteAllText(paths.SettingsFilePath, forwardCompatibleJson, new UTF8Encoding(false));

        var store = new JsonSettingsStore(paths);
        SettingsLoadResult loaded = store.LoadAsync().GetAwaiter().GetResult();

        AssertEx.Equal(StorageLoadStatus.Loaded, loaded.Status);
        AssertEx.Equal("Light", loaded.Settings.Theme);
        AssertEx.Equal("zh-CN", loaded.Settings.Culture);
        AssertEx.Equal(42UL, loaded.Settings.ModeOne.Seed);
        AssertEx.Equal(0, loaded.Settings.ModeOne.StrokeStabilization);
        AssertEx.Equal("Normal", loaded.Settings.ModeTwo.Difficulty);
        AssertEx.Equal("Normal", loaded.Settings.ModeThree.Difficulty);

        store.SaveAsync(loaded.Settings).GetAwaiter().GetResult();
        string normalizedJson = File.ReadAllText(paths.SettingsFilePath, Encoding.UTF8);
        AssertEx.False(
            normalizedJson.Contains("futureRootField", StringComparison.Ordinal),
            "未知根字段不应进入规范化设置。");
        AssertEx.False(
            normalizedJson.Contains("futureModeField", StringComparison.Ordinal),
            "未知模式字段不应进入规范化设置。");
        AssertNoTemporaryFiles(directory.RootPath);
    }

    private static void HistoryEvictsOldestEntries()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        var store = new JsonExerciseHistoryStore(
            paths,
            new HistoryStoreOptions { MaxEntries = 3 });

        for (var index = 0; index < 5; index++)
        {
            store.AppendAsync(CreateHistoryEntry(index)).GetAwaiter().GetResult();
        }

        HistoryLoadResult loaded = store.LoadAsync().GetAwaiter().GetResult();
        AssertEx.Equal(StorageLoadStatus.Loaded, loaded.Status);
        AssertEx.Equal(3, loaded.Entries.Count);
        AssertEx.Equal(2L, loaded.Entries[0].ExerciseIndex);
        AssertEx.Equal(3L, loaded.Entries[1].ExerciseIndex);
        AssertEx.Equal(4L, loaded.Entries[2].ExerciseIndex);
        AssertEx.Equal(TrainingModeKind.LineFollow, loaded.Entries[2].Mode);
        AssertEx.Equal(ExerciseOutcome.Completed, loaded.Entries[2].Outcome);
        AssertEx.Near(94.0, loaded.Entries[2].TotalScore ?? double.NaN, 0.0);
        AssertEx.Near(84.0, loaded.Entries[2].ComponentScores["accuracy"], 0.0);
        AssertNoTemporaryFiles(directory.RootPath);
    }

    private static void SignedComponentScoresRoundTrip()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        var store = new JsonExerciseHistoryStore(paths);
        ExerciseHistoryEntry entry = CreateHistoryEntry(0);
        entry.Mode = TrainingModeKind.ColorMatch;
        entry.TotalScore = 125.0;
        entry.ComponentScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["lightnessDelta"] = -18.75,
            ["saturationDelta"] = 12.5,
            ["hueDeltaDegrees"] = -173.25,
            ["finiteValueAbovePercentageRange"] = 125.5,
            ["notFinite"] = double.NaN,
        };

        store.AppendAsync(entry).GetAwaiter().GetResult();
        HistoryLoadResult loaded = store.LoadAsync().GetAwaiter().GetResult();

        AssertEx.Equal(1, loaded.Entries.Count);
        ExerciseHistoryEntry roundTripped = loaded.Entries[0];
        AssertEx.Near(100.0, roundTripped.TotalScore ?? double.NaN, 0.0);
        AssertEx.Equal(4, roundTripped.ComponentScores.Count);
        AssertEx.Near(-18.75, roundTripped.ComponentScores["lightnessDelta"], 0.0);
        AssertEx.Near(12.5, roundTripped.ComponentScores["saturationDelta"], 0.0);
        AssertEx.Near(-173.25, roundTripped.ComponentScores["hueDeltaDegrees"], 0.0);
        AssertEx.Near(
            125.5,
            roundTripped.ComponentScores["finiteValueAbovePercentageRange"],
            0.0);
        AssertEx.False(
            roundTripped.ComponentScores.ContainsKey("notFinite"),
            "Non-finite component values must not be persisted.");
        AssertNoTemporaryFiles(directory.RootPath);
    }

    private static void CorruptHistoryIsBackedUpAndRecovered()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        Directory.CreateDirectory(paths.RootDirectory);
        const string corruptJson = "[ definitely-not-the-history-schema";
        File.WriteAllText(paths.HistoryFilePath, corruptJson, new UTF8Encoding(false));

        var store = new JsonExerciseHistoryStore(paths);
        HistoryLoadResult recovered = store.LoadAsync().GetAwaiter().GetResult();

        AssertEx.Equal(StorageLoadStatus.CorruptRecovered, recovered.Status);
        AssertEx.Equal(0, recovered.Entries.Count);
        string backupPath = recovered.RecoveryBackupPath
            ?? throw new InvalidOperationException("恢复结果未报告损坏历史备份路径。");
        AssertStrictDescendant(paths.RecoveryDirectory, backupPath);
        AssertEx.Equal(corruptJson, File.ReadAllText(backupPath, Encoding.UTF8));

        store.AppendAsync(CreateHistoryEntry(7)).GetAwaiter().GetResult();
        HistoryLoadResult afterAppend = store.LoadAsync().GetAwaiter().GetResult();
        AssertEx.Equal(StorageLoadStatus.Loaded, afterAppend.Status);
        AssertEx.Equal(1, afterAppend.Entries.Count);
        AssertEx.Equal(7L, afterAppend.Entries[0].ExerciseIndex);
        AssertNoTemporaryFiles(directory.RootPath);
    }

    private static void RollingLogIsBounded()
    {
        using var directory = InfrastructureTestDirectory.Create();
        var paths = new DrawAimDataPaths(directory.RootPath);
        const long maxFileBytes = 65_536;
        const int retainedFiles = 2;
        var logger = new RollingFileLogger(
            paths,
            new RollingFileLoggerOptions
            {
                FileNamePrefix = "infrastructure-test",
                MaxFileBytes = maxFileBytes,
                MaxRetainedFiles = retainedFiles,
                MaxEntryCharacters = 16_000,
            });

        for (var index = 0; index < 20; index++)
        {
            bool written = logger.WriteAsync(
                    DrawAimLogLevel.Information,
                    $"marker-{index:D2} {new string('x', 12_000)}")
                .GetAwaiter()
                .GetResult();
            AssertEx.True(written, $"第 {index} 条测试日志写入失败。");
        }

        string[] logFiles = Directory.GetFiles(paths.LogsDirectory, "infrastructure-test-*.log");
        AssertEx.Equal(retainedFiles, logFiles.Length);
        foreach (string logFile in logFiles)
        {
            long length = new FileInfo(logFile).Length;
            AssertEx.True(length > 0, "滚动日志不应为空。");
            AssertEx.True(length <= maxFileBytes, $"日志文件超过上限：{length} > {maxFileBytes}。");
        }

        string newestLog = logFiles
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(static path => path, StringComparer.Ordinal)
            .First();
        AssertEx.True(
            File.ReadAllText(newestLog, Encoding.UTF8).Contains("marker-19", StringComparison.Ordinal),
            "最新滚动日志缺少最后一条记录。");
    }

    private static void DataRootEnvironmentVariableRedirectsAllData()
    {
        using var directory = InfrastructureTestDirectory.Create();
        string redirectedRoot = Path.Combine(directory.RootPath, "redirected-data");
        string? originalValue = Environment.GetEnvironmentVariable(
            DrawAimDataPaths.DataRootEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                DrawAimDataPaths.DataRootEnvironmentVariable,
                redirectedRoot);

            DrawAimDataPaths paths = DrawAimDataPaths.Resolve();
            AssertEx.Equal(Path.GetFullPath(redirectedRoot), paths.RootDirectory);
            AssertStrictDescendant(paths.RootDirectory, paths.SettingsFilePath);
            AssertStrictDescendant(paths.RootDirectory, paths.HistoryFilePath);
            AssertStrictDescendant(paths.RootDirectory, paths.LogsDirectory);
            AssertStrictDescendant(paths.RootDirectory, paths.RecoveryDirectory);

            var store = new JsonSettingsStore();
            store.SaveAsync(AppSettings.CreateDefault()).GetAwaiter().GetResult();
            AssertEx.True(
                File.Exists(Path.Combine(redirectedRoot, "settings.json")),
                "环境变量重定向后的 settings.json 未写入指定目录。");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DrawAimDataPaths.DataRootEnvironmentVariable,
                originalValue);
        }
    }

    private static ExerciseHistoryEntry CreateHistoryEntry(int index) =>
        new()
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"),
            TimestampUtc = new DateTimeOffset(2026, 8, 3, 0, 0, index, TimeSpan.Zero),
            Mode = TrainingModeKind.LineFollow,
            Outcome = ExerciseOutcome.Completed,
            Seed = 1234,
            ExerciseIndex = index,
            GeneratorVersion = "LineGeneratorV1",
            SettingsFingerprint = "settings-v1",
            StrokeStabilization = 0,
            StabilizerVersion = "StrokeStabilizerV1",
            ScoringVersion = "LineScoreV1",
            TotalScore = 90.0 + index,
            ComponentScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["accuracy"] = 80.0 + index,
            },
        };

    private static void AssertNoTemporaryFiles(string rootPath)
    {
        string[] temporaryFiles = Directory.GetFiles(rootPath, "*.tmp", SearchOption.AllDirectories);
        AssertEx.Equal(0, temporaryFiles.Length, "原子写入遗留了临时文件。");
    }

    private static void AssertStrictDescendant(string expectedParent, string candidate)
    {
        string parentPath = Path.GetFullPath(expectedParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidatePath = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(parentPath, candidatePath);

        bool isDescendant = !Path.IsPathRooted(relative) &&
            !string.Equals(relative, ".", StringComparison.Ordinal) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        AssertEx.True(isDescendant, $"路径不在预期目录内：{candidatePath}");
    }

    private sealed class InfrastructureTestDirectory : IDisposable
    {
        private const string DirectoryPrefix = "case-";
        private bool _disposed;

        private InfrastructureTestDirectory(string containerPath, string rootPath)
        {
            ContainerPath = containerPath;
            RootPath = rootPath;
        }

        public string ContainerPath { get; }

        public string RootPath { get; }

        public static InfrastructureTestDirectory Create()
        {
            string projectDirectory = FindTestProjectDirectory();
            string containerPath = Path.GetFullPath(
                Path.Combine(projectDirectory, ".test-artifacts", "infrastructure"));
            Directory.CreateDirectory(containerPath);
            EnsureNotReparsePoint(containerPath);

            string rootPath = Path.Combine(containerPath, $"{DirectoryPrefix}{Guid.NewGuid():N}");
            ValidateCleanupTarget(containerPath, rootPath, requireExisting: false);
            Directory.CreateDirectory(rootPath);
            EnsureNotReparsePoint(rootPath);
            return new InfrastructureTestDirectory(containerPath, rootPath);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            ValidateCleanupTarget(ContainerPath, RootPath, requireExisting: true);
            EnsureTreeContainsNoReparsePoints(RootPath);
            Directory.Delete(RootPath, recursive: true);
        }

        private static string FindTestProjectDirectory()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "DrawAim.Tests.csproj")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("无法定位 DrawAim.Tests 项目目录。");
        }

        private static void ValidateCleanupTarget(
            string containerPath,
            string rootPath,
            bool requireExisting)
        {
            string expectedContainer = Path.GetFullPath(containerPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Directory.GetParent(candidate)?.FullName;
            string leafName = Path.GetFileName(candidate);

            if (!string.Equals(parent, expectedContainer, StringComparison.OrdinalIgnoreCase) ||
                !leafName.StartsWith(DirectoryPrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(leafName.AsSpan(DirectoryPrefix.Length), "N", out _))
            {
                throw new InvalidOperationException($"拒绝清理未经验证的测试目录：{candidate}");
            }

            AssertStrictDescendant(expectedContainer, candidate);
            EnsureNotReparsePoint(expectedContainer);
            if (requireExisting)
            {
                EnsureNotReparsePoint(candidate);
            }
        }

        private static void EnsureTreeContainsNoReparsePoints(string rootPath)
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                string current = pendingDirectories.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException($"拒绝清理包含重解析点的测试目录：{entry}");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entry);
                    }
                }
            }
        }

        private static void EnsureNotReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"测试目录不能是重解析点：{path}");
            }
        }
    }
}
