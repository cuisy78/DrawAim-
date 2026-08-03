using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DrawAim.Core.Color;
using DrawAim.Core.Generation;
using DrawAim.Core.Geometry;
using DrawAim.Core.Scoring;
using DrawAim.App.Services;
using DrawAim.Infrastructure.History;
using DrawAim.Infrastructure.Logging;
using DrawAim.Infrastructure.Settings;
using DrawAim.Infrastructure.Storage;

namespace DrawAim.App;

public partial class MainWindow : Window
{
    private enum AppRoute
    {
        Home,
        Mode1,
        Mode2,
        Mode3,
        Statistics,
    }

    private readonly TargetLineGenerator _lineGenerator = new();
    private readonly MultiLineGenerator _multiLineGenerator = new();
    private readonly TargetColorGenerator _colorGenerator = new();
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonExerciseHistoryStore _historyStore;
    private readonly RollingFileLogger _logger;
    private readonly LatestWinsCoordinator<Mode2PreviewWork, MultiLineScoreResult> _mode2PreviewCoordinator;
    private readonly List<ScoreListItem> _scoreRows = [];
    private readonly List<double> _sessionScores = [];
    private readonly object _historyWriteSync = new();
    private readonly HashSet<Task> _pendingHistoryWrites = [];

    private AppSettings _settings = AppSettings.CreateDefault();
    private AppRoute _activeRoute = AppRoute.Home;
    private TargetCurve? _mode1Target;
    private MultiLineExercise? _mode2Exercise;
    private SrgbColor _mode3Target = new(0.5, 0.5, 0.5);
    private SrgbColor _mode3Selected = new(1, 1, 1);
    private int _mode1ExerciseIndex;
    private int _mode2ExerciseIndex;
    private int _mode3ExerciseIndex;
    private long _mode1QuestionVersion;
    private long _mode2QuestionVersion;
    private string _mode1QuestionFingerprint = "unknown";
    private string _mode2QuestionFingerprint = "unknown";
    private string _mode3QuestionFingerprint = "unknown";
    private ulong _mode1QuestionSeed;
    private ulong _mode2QuestionSeed;
    private ulong _mode3QuestionSeed;
    private double _mode1QuestionTolerance = 18;
    private double _mode1QuestionCanvasWidth;
    private double _mode1QuestionCanvasHeight;
    private bool _isPaused;
    private bool _isLightTheme;
    private bool _mode1QuestionActive;
    private bool _mode2Submitting;
    private bool _mode3Submitted;
    private bool _mode3QuestionActive;
    private bool _mode3QuestionPracticeMode = true;
    private bool _isClosing;
    private bool _isUiInitialized;
    private CancellationTokenSource? _mode1AdvanceCancellation;
    private CancellationTokenSource? _mode1LiveCancellation;
    private CancellationTokenSource? _mode1ResizeCancellation;
    private CancellationTokenSource? _mode2FinalCancellation;
    private CancellationTokenSource? _mode2AdvanceCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _isUiInitialized = true;

        DrawAimDataPaths paths = ResolveDataPathsSafely();
        _settingsStore = new JsonSettingsStore(paths);
        _historyStore = new JsonExerciseHistoryStore(paths);
        _logger = new RollingFileLogger(paths);
        _mode2PreviewCoordinator = new LatestWinsCoordinator<Mode2PreviewWork, MultiLineScoreResult>(
            static (work, token) => new ValueTask<MultiLineScoreResult>(Task.Run(
                () => MultiLineScoreV1.Score(
                    work.Exercise.Lines,
                    work.Answer,
                    toleranceNormalized: 0.022,
                    gridResolution: 192,
                    token),
                token)));

        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        Mode1Canvas.StrokeStarted += Mode1Canvas_StrokeStarted;
        Mode1Canvas.StrokeUpdated += Mode1Canvas_StrokeUpdated;
        Mode1Canvas.StrokeCancelled += Mode1Canvas_StrokeCancelled;
        Mode2Canvas.StrokeUpdated += Mode2Canvas_StrokeUpdated;
        Mode2Canvas.StrokesChanged += Mode2Canvas_StrokesChanged;
        Mode2Canvas.StrokeCancelled += Mode2Canvas_StrokeCancelled;
        Mode3Canvas.StrokeCancelled += (_, _) => { };
        Mode3ColorPicker_SelectedColorChanged(
            Mode3ColorPicker,
            new Controls.HsvColorChangedEventArgs(
                Mode3ColorPicker.SelectedHsv,
                Mode3ColorPicker.SelectedColor));
    }

    private static DrawAimDataPaths ResolveDataPathsSafely()
    {
        try
        {
            return DrawAimDataPaths.Resolve();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            string fallback = Path.Combine(Path.GetTempPath(), "DrawAim", "local-data");
            return new DrawAimDataPaths(fallback);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitInitialWindowToWorkArea();
        ApplyResponsiveLayout();
        try
        {
            SettingsLoadResult settingsResult = await _settingsStore.LoadAsync();
            _settings = settingsResult.Settings;
            ApplyLoadedSettings(_settings);

            HistoryLoadResult history = await _historyStore.LoadAsync();
            LoadHistoryRows(history.Entries);
            if (settingsResult.Status == StorageLoadStatus.CorruptRecovered ||
                history.Status == StorageLoadStatus.CorruptRecovered)
            {
                RouteSubtitle.Text = "已恢复损坏的本地数据，原文件保存在 recovery 目录";
            }
        }
        catch (Exception exception)
        {
            RouteSubtitle.Text = "本地设置暂时不可用，本次使用安全默认值";
            await LogSafelyAsync(DrawAimLogLevel.Warning, "启动时读取本地数据失败。", exception);
        }

        NavigateTo(AppRoute.Home);
        if (!_settings.HasCompletedFirstRunGuide)
        {
            FirstRunOverlay.Visibility = Visibility.Visible;
        }
    }

    private void ApplyLoadedSettings(AppSettings settings)
    {
        _isLightTheme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        ApplyThemeResources();
        Mode1Stability.Value = settings.ModeOne.StrokeStabilization;
        Mode2Stability.Value = settings.ModeTwo.StrokeStabilization;
        Mode3Stability.Value = settings.ModeThree.StrokeStabilization;
        Mode3BrushSize.Value = settings.BrushSize;
        ApplyExtendedLoadedSettings(settings);

        if (settings.ModeOne.Seed != 0)
        {
            Mode1Seed.Text = settings.ModeOne.Seed.ToString(CultureInfo.InvariantCulture);
        }

        if (settings.ModeTwo.Seed != 0)
        {
            Mode2Seed.Text = settings.ModeTwo.Seed.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void LoadHistoryRows(IReadOnlyList<ExerciseHistoryEntry> entries)
    {
        foreach (ExerciseHistoryEntry entry in entries
                     .Where(static item => item.Outcome == ExerciseOutcome.Completed && item.TotalScore.HasValue)
                     .OrderByDescending(static item => item.TimestampUtc))
        {
            _scoreRows.Add(new ScoreListItem
            {
                Time = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                Mode = ModeDisplayName(entry.Mode),
                Score = $"{entry.TotalScore!.Value:F1}",
                Stability = entry.StrokeStabilization == 0 ? "无辅助" : $"{entry.StrokeStabilization}",
                Identity = $"{entry.Seed} / {entry.ExerciseIndex}",
                NumericScore = entry.TotalScore.Value,
                ScoringVersion = entry.ScoringVersion,
                GeneratorVersion = entry.GeneratorVersion,
                StabilizerVersion = entry.StabilizerVersion,
                SettingsFingerprint = entry.SettingsFingerprint,
                StabilityLevel = entry.StrokeStabilization,
            });
        }

        RefreshStatistics();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out AppRoute route))
        {
            return;
        }

        NavigateTo(route);
    }

    private void NavigateTo(AppRoute route)
    {
        CancelAllActiveStrokes();
        _isPaused = false;
        PauseOverlay.Visibility = Visibility.Collapsed;
        _activeRoute = route;

        HomePage.Visibility = route == AppRoute.Home ? Visibility.Visible : Visibility.Collapsed;
        Mode1Page.Visibility = route == AppRoute.Mode1 ? Visibility.Visible : Visibility.Collapsed;
        Mode2Page.Visibility = route == AppRoute.Mode2 ? Visibility.Visible : Visibility.Collapsed;
        Mode3Page.Visibility = route == AppRoute.Mode3 ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPage.Visibility = route == AppRoute.Statistics ? Visibility.Visible : Visibility.Collapsed;

        (RouteTitle.Text, RouteSubtitle.Text) = route switch
        {
            AppRoute.Home => ("首页", "选择一项训练开始"),
            AppRoute.Mode1 => ("模式一 · 线条跟随", "一笔作答，抬笔自动提交"),
            AppRoute.Mode2 => ("模式二 · 观察复制", "只比较最终几何，玩家手动提交"),
            AppRoute.Mode3 => ("模式三 · 颜色匹配", "提交当前选中色，试色笔迹不计分"),
            AppRoute.Statistics => ("训练统计", "本地成绩与本次训练概览"),
            _ => ("DrawAim", string.Empty),
        };

        switch (route)
        {
            case AppRoute.Mode1 when _mode1Target is null:
                GenerateMode1Question();
                break;
            case AppRoute.Mode2 when _mode2Exercise is null:
                GenerateMode2Question();
                break;
            case AppRoute.Mode3:
                if (!_mode3QuestionActive)
                {
                    GenerateMode3Question();
                }

                break;
            case AppRoute.Statistics:
                RefreshStatistics();
                break;
        }

        ApplyInputEnabledState();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _isLightTheme = !_isLightTheme;
        ApplyThemeResources();
    }

    private void ApplyThemeResources()
    {
        ResourceDictionary resources = Application.Current.Resources;
        if (_isLightTheme)
        {
            SetBrush(resources, "BackgroundBrush", Color.FromRgb(241, 244, 248));
            SetBrush(resources, "SidebarBrush", Color.FromRgb(231, 236, 242));
            SetBrush(resources, "SurfaceBrush", Color.FromRgb(251, 252, 253));
            SetBrush(resources, "SurfaceRaisedBrush", Color.FromRgb(226, 232, 239));
            SetBrush(resources, "BorderBrush", Color.FromRgb(198, 207, 218));
            SetBrush(resources, "TextBrush", Color.FromRgb(28, 35, 45));
            SetBrush(resources, "MutedTextBrush", Color.FromRgb(91, 104, 121));
            SetBrush(resources, "CanvasBrush", Color.FromRgb(255, 255, 255));
            SetBrush(resources, "CanvasStrokeBrush", Color.FromRgb(0, 0, 0));
            SetBrush(resources, "CanvasTargetBrush", Color.FromRgb(116, 126, 140));
            ThemeButtonText.Text = "切换深色主题";
        }
        else
        {
            SetBrush(resources, "BackgroundBrush", Color.FromRgb(16, 20, 28));
            SetBrush(resources, "SidebarBrush", Color.FromRgb(20, 26, 36));
            SetBrush(resources, "SurfaceBrush", Color.FromRgb(26, 33, 45));
            SetBrush(resources, "SurfaceRaisedBrush", Color.FromRgb(34, 43, 57));
            SetBrush(resources, "BorderBrush", Color.FromRgb(48, 59, 76));
            SetBrush(resources, "TextBrush", Color.FromRgb(243, 246, 250));
            SetBrush(resources, "MutedTextBrush", Color.FromRgb(156, 169, 186));
            SetBrush(resources, "CanvasBrush", Color.FromRgb(23, 28, 37));
            SetBrush(resources, "CanvasStrokeBrush", Color.FromRgb(247, 249, 252));
            SetBrush(resources, "CanvasTargetBrush", Color.FromRgb(132, 144, 163));
            ThemeButtonText.Text = "切换浅色主题";
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color) =>
        resources[key] = new SolidColorBrush(color);

    private void CloseGuide_Click(object sender, RoutedEventArgs e)
    {
        FirstRunOverlay.Visibility = Visibility.Collapsed;
        _settings.HasCompletedFirstRunGuide = true;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPaused)
        {
            CancelAllActiveStrokes();
        }

        _isPaused = !_isPaused;
        PauseOverlay.Visibility = _isPaused ? Visibility.Visible : Visibility.Collapsed;
        ApplyInputEnabledState();
    }

    private async Task DelayWhileUnpausedAsync(
        TimeSpan activeDuration,
        CancellationToken cancellationToken = default)
    {
        TimeSpan remaining = activeDuration;
        TimeSpan maximumSlice = TimeSpan.FromMilliseconds(25);
        while (remaining > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isPaused)
            {
                await Task.Delay(maximumSlice, cancellationToken);
                continue;
            }

            TimeSpan slice = remaining < maximumSlice ? remaining : maximumSlice;
            long started = Stopwatch.GetTimestamp();
            await Task.Delay(slice, cancellationToken);
            if (!_isPaused)
            {
                remaining -= Stopwatch.GetElapsedTime(started);
            }
        }
    }

    private void ApplyInputEnabledState()
    {
        Mode1Canvas.IsInputEnabled =
            _activeRoute == AppRoute.Mode1 && !_isPaused && _mode1QuestionActive;
        Mode2Canvas.IsInputEnabled =
            _activeRoute == AppRoute.Mode2 && !_isPaused && !_mode2Submitting;
        Mode3Canvas.IsInputEnabled =
            _activeRoute == AppRoute.Mode3 && !_isPaused && !_mode3Submitted;
        Mode3ColorPicker.IsEnabled =
            _activeRoute == AppRoute.Mode3 && !_isPaused && !_mode3Submitted;
    }

    private void CancelAllActiveStrokes()
    {
        bool mode1WasDrawing = Mode1Canvas.IsDrawing;
        Mode1Canvas.CancelActiveStroke();
        Mode2Canvas.CancelActiveStroke();
        Mode3Canvas.CancelActiveStroke();
        if (mode1WasDrawing)
        {
            Mode1Instruction.Text = "系统中断：本笔未提交，也不计入题数。";
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e) => CancelAllActiveStrokes();

    private void Window_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        CancelAllActiveStrokes();
        Mode1Canvas.InvalidateVisual();
        Mode2ReferenceCanvas.InvalidateVisual();
        Mode2Canvas.InvalidateVisual();
        Mode3Canvas.InvalidateVisual();
        ApplyResponsiveLayout();
        ResizeMode2Squares();
        ScheduleMode1CanvasRefresh();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Mode1Canvas.IsDrawing || Mode2Canvas.IsDrawing || Mode3Canvas.IsDrawing)
        {
            CancelAllActiveStrokes();
        }

        ApplyResponsiveLayout();
        ScheduleMode1CanvasRefresh();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
            Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            Pause_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isPaused)
        {
            Pause_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (_isPaused)
        {
            e.Handled = true;
            return;
        }

        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (_activeRoute)
        {
            case AppRoute.Mode1 when e.Key == Key.N:
                Mode1Skip_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode2 when control && e.Key == Key.Z:
                Mode2Undo_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode2 when control && e.Key == Key.Y:
                Mode2Redo_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode2 when e.Key == Key.Delete:
                Mode2Clear_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode2 when e.Key == Key.Enter:
                Mode2Submit_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode2 when e.Key == Key.N:
                Mode2NewQuestion_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode3 when control && e.Key == Key.Z:
                Mode3Undo_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode3 when control && e.Key == Key.Y:
                Mode3Redo_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode3 when e.Key == Key.Delete:
                Mode3Clear_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode3 when e.Key == Key.Enter:
                Mode3Submit_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case AppRoute.Mode3 when e.Key == Key.N:
                Mode3NewQuestion_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _mode1AdvanceCancellation?.Cancel();
        _mode1LiveCancellation?.Cancel();
        _mode1ResizeCancellation?.Cancel();
        _mode2FinalCancellation?.Cancel();
        _mode2AdvanceCancellation?.Cancel();
        _mode2PreviewCoordinator.CancelCurrent();
        _mode2PreviewCoordinator.Dispose();
        CancelAllActiveStrokes();

        _settings.Theme = _isLightTheme ? "Light" : "Dark";
        _settings.BrushSize = Mode3BrushSize.Value;
        _settings.ModeOne.StrokeStabilization = (int)Math.Round(Mode1Stability.Value);
        _settings.ModeTwo.StrokeStabilization = (int)Math.Round(Mode2Stability.Value);
        _settings.ModeThree.StrokeStabilization = (int)Math.Round(Mode3Stability.Value);
        _settings.ModeOne.Seed = ParseSeed(Mode1Seed.Text, 20260803);
        _settings.ModeTwo.Seed = ParseSeed(Mode2Seed.Text, 20260803);
        CaptureExtendedSettings();

        try
        {
            _settingsStore.SaveAsync(_settings).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _ = LogSafelyAsync(DrawAimLogLevel.Warning, "关闭时保存设置失败。", exception);
        }

        try
        {
            FlushPendingHistoryWritesAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _ = LogSafelyAsync(DrawAimLogLevel.Warning, "关闭时等待训练历史写入失败。", exception);
        }
    }

    private async Task RecordCompletedAsync(
        TrainingModeKind mode,
        double score,
        int stability,
        ulong seed,
        int exerciseIndex,
        string generatorVersion,
        string scoringVersion,
        IReadOnlyDictionary<string, double> components,
        string settingsFingerprint,
        string stabilizerVersion)
    {
        var entry = new ExerciseHistoryEntry
        {
            Mode = mode,
            Outcome = ExerciseOutcome.Completed,
            Seed = seed,
            ExerciseIndex = exerciseIndex,
            GeneratorVersion = generatorVersion,
            SettingsFingerprint = settingsFingerprint,
            StrokeStabilization = stability,
            StabilizerVersion = stabilizerVersion,
            ScoringVersion = scoringVersion,
            TotalScore = score,
            ComponentScores = new Dictionary<string, double>(components, StringComparer.OrdinalIgnoreCase),
        };

        AddScoreRow(entry);
        try
        {
            await _historyStore.AppendAsync(entry).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await LogSafelyAsync(DrawAimLogLevel.Warning, "保存训练成绩失败。", exception)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordSkippedAsync(
        TrainingModeKind mode,
        ulong seed,
        int exerciseIndex,
        string generatorVersion,
        string settingsFingerprint)
    {
        try
        {
            await _historyStore.AppendAsync(new ExerciseHistoryEntry
            {
                Mode = mode,
                Outcome = ExerciseOutcome.Skipped,
                Seed = seed,
                ExerciseIndex = exerciseIndex,
                GeneratorVersion = generatorVersion,
                SettingsFingerprint = settingsFingerprint,
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await LogSafelyAsync(DrawAimLogLevel.Warning, "保存跳题记录失败。", exception)
                .ConfigureAwait(false);
        }
    }

    private void TrackHistoryWrite(Task writeTask)
    {
        ArgumentNullException.ThrowIfNull(writeTask);
        lock (_historyWriteSync)
        {
            _pendingHistoryWrites.Add(writeTask);
        }

        _ = ObserveHistoryWriteAsync(writeTask);
    }

    private async Task ObserveHistoryWriteAsync(Task writeTask)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_historyWriteSync)
            {
                _pendingHistoryWrites.Remove(writeTask);
            }
        }
    }

    private async Task FlushPendingHistoryWritesAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_historyWriteSync)
            {
                snapshot = _pendingHistoryWrites.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
    }

    private void AddScoreRow(ExerciseHistoryEntry entry)
    {
        double score = entry.TotalScore ?? 0;
        _sessionScores.Add(score);
        _scoreRows.Insert(0, new ScoreListItem
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            Mode = ModeDisplayName(entry.Mode),
            Score = $"{score:F1}",
            Stability = entry.StrokeStabilization == 0 ? "无辅助" : entry.StrokeStabilization.ToString(CultureInfo.InvariantCulture),
            Identity = $"{entry.Seed} / {entry.ExerciseIndex}",
            NumericScore = score,
            ScoringVersion = entry.ScoringVersion,
            GeneratorVersion = entry.GeneratorVersion,
            StabilizerVersion = entry.StabilizerVersion,
            SettingsFingerprint = entry.SettingsFingerprint,
            StabilityLevel = entry.StrokeStabilization,
        });
        if (_scoreRows.Count > 10_000)
        {
            _scoreRows.RemoveRange(10_000, _scoreRows.Count - 10_000);
        }

        RefreshStatistics();
    }

    private void RefreshStatistics()
    {
        int completed = _sessionScores.Count;
        string average = completed == 0 ? "—" : $"{_sessionScores.Average():F1}";
        string best = completed == 0 ? "—" : $"{_sessionScores.Max():F1}";

        TopCompletedText.Text = completed.ToString(CultureInfo.InvariantCulture);
        TopAverageText.Text = average;
        HomeCompletedText.Text = completed.ToString(CultureInfo.InvariantCulture);
        HomeAverageText.Text = average;
        StatsCompleted.Text = completed.ToString(CultureInfo.InvariantCulture);
        StatsAverage.Text = average;
        StatsBest.Text = best;
        RecentScoresList.ItemsSource = null;
        RecentScoresList.ItemsSource = _scoreRows.Take(50).ToArray();
        GroupedBestList.ItemsSource = null;
        GroupedBestList.ItemsSource = _scoreRows
            .GroupBy(static row => new
            {
                row.Mode,
                row.StabilityLevel,
                row.ScoringVersion,
                row.GeneratorVersion,
                row.StabilizerVersion,
                row.SettingsFingerprint,
            })
            .Select(static group => new BestScoreListItem
            {
                Mode = group.Key.Mode,
                Stability = group.Key.StabilityLevel == 0
                    ? "无辅助"
                    : group.Key.StabilityLevel.ToString(CultureInfo.InvariantCulture),
                ScoringVersion = group.Key.ScoringVersion,
                GeneratorVersion = group.Key.GeneratorVersion,
                StabilizerVersion = group.Key.StabilizerVersion,
                Settings = $"{group.Key.SettingsFingerprint};gen={group.Key.GeneratorVersion};stabilizer={group.Key.StabilizerVersion}",
                Score = $"{group.Max(static row => row.NumericScore):F1}",
                NumericScore = group.Max(static row => row.NumericScore),
            })
            .OrderByDescending(static row => row.NumericScore)
            .Take(50)
            .ToArray();
    }

    private string BuildFingerprint(
        TrainingModeKind mode,
        double? logicalCanvasWidth = null,
        double? logicalCanvasHeight = null)
    {
        switch (mode)
        {
            case TrainingModeKind.LineFollow:
            {
                (double straight, double cShape, double sShape) = GetMode1LineWeights();
                (double directionMinimum, double directionMaximum) = NormalizeOrderedRange(
                    Mode1DirectionMin.Value,
                    Mode1DirectionMax.Value,
                    0,
                    360,
                    1);
                double canvasWidth = logicalCanvasWidth ??
                    (Mode1Canvas.ActualWidth >= 200 ? Mode1Canvas.ActualWidth : 780);
                double canvasHeight = logicalCanvasHeight ??
                    (Mode1Canvas.ActualHeight >= 200 ? Mode1Canvas.ActualHeight : 580);
                return FormattableString.Invariant(
                    $"kind={Mode1LineKind.SelectedIndex};weights={straight:F1},{cShape:F1},{sShape:F1};d={Mode1Difficulty.Value:F0};len={Mode1Length.Value:F0};curv={Mode1Curvature.Value:F0};dir={directionMinimum:F0}-{directionMaximum:F0};targetWidth={Mode1TargetWidth.Value:F1};tol={Mode1Tolerance.Value:F1};hint={Mode1ShowHint.IsChecked == true};canvas={canvasWidth:F1}x{canvasHeight:F1}");
            }
            case TrainingModeKind.ObservationCopy:
            {
                int minimumCount = Mode2UseCountRange.IsChecked == true
                    ? NormalizeMode2Count(Mode2MinCount.Value)
                    : NormalizeMode2Count(Mode2LineCount.Value);
                int maximumCount = Mode2UseCountRange.IsChecked == true
                    ? NormalizeMode2Count(Mode2MaxCount.Value)
                    : minimumCount;
                if (minimumCount > maximumCount)
                {
                    (minimumCount, maximumCount) = (maximumCount, minimumCount);
                }

                (double minimumLength, double maximumLength) = NormalizeOrderedRange(
                    Mode2MinLength.Value,
                    Mode2MaxLength.Value,
                    10,
                    70,
                    0);
                (double minimumCurvature, double maximumCurvature) = NormalizeOrderedRange(
                    Mode2MinCurvature.Value,
                    Mode2MaxCurvature.Value,
                    0,
                    45,
                    0);
                return FormattableString.Invariant(
                    $"count={minimumCount}-{maximumCount};weights={NormalizeMode2Weight(Mode2StraightWeight.Value):F1},{NormalizeMode2Weight(Mode2CWeight.Value):F1},{NormalizeMode2Weight(Mode2SWeight.Value):F1};d={Mode2Difficulty.Value:F0};len={minimumLength:F0}-{maximumLength:F0};curv={minimumCurvature:F0}-{maximumCurvature:F0};cross={Mode2AllowIntersections.IsChecked == true}");
            }
            case TrainingModeKind.ColorMatch:
                return FormattableString.Invariant(
                    $"d={Mode3Difficulty.Value:F0};white={Mode3IncludeWhite.IsChecked == true};black={Mode3IncludeBlack.IsChecked == true};low={Mode3IncludeLowChroma.IsChecked == true};practice={Mode3PracticeMode.IsChecked == true}");
            default:
                return "unknown";
        }
    }

    private static string ModeDisplayName(TrainingModeKind mode) => mode switch
    {
        TrainingModeKind.LineFollow => "线条跟随",
        TrainingModeKind.ObservationCopy => "观察复制",
        TrainingModeKind.ColorMatch => "颜色匹配",
        _ => "未知",
    };

    private async Task LogSafelyAsync(DrawAimLogLevel level, string message, Exception? exception = null)
    {
        try
        {
            await _logger.WriteAsync(level, message, exception);
        }
        catch (OperationCanceledException)
        {
            // Logging is best effort during shutdown.
        }
    }

    private static ulong ParseSeed(string? value, ulong fallback) =>
        ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong seed)
            ? seed
            : fallback;

    private static string StabilityLabel(int level) => level switch
    {
        0 => "0 · 关闭",
        <= 24 => $"{level} · 轻微",
        <= 49 => $"{level} · 中等",
        <= 74 => $"{level} · 明显",
        <= 99 => $"{level} · 强",
        _ => "100 · 极强",
    };
}

internal sealed record Mode2PreviewWork(
    MultiLineExercise Exercise,
    IReadOnlyList<LogicalStroke> Answer);

public sealed class ScoreListItem
{
    public string Time { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public string Stability { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public double NumericScore { get; init; }
    public int StabilityLevel { get; init; }
    public string ScoringVersion { get; init; } = string.Empty;
    public string GeneratorVersion { get; init; } = string.Empty;
    public string StabilizerVersion { get; init; } = string.Empty;
    public string SettingsFingerprint { get; init; } = string.Empty;
}

public sealed class BestScoreListItem
{
    public string Mode { get; init; } = string.Empty;
    public string Stability { get; init; } = string.Empty;
    public string ScoringVersion { get; init; } = string.Empty;
    public string GeneratorVersion { get; init; } = string.Empty;
    public string StabilizerVersion { get; init; } = string.Empty;
    public string Settings { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public double NumericScore { get; init; }
}
