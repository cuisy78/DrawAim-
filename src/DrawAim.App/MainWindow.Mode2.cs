using System.Globalization;
using System.Windows;
using DrawAim.App.Controls;
using DrawAim.App.Services;
using DrawAim.Core.Generation;
using DrawAim.Core.Geometry;
using DrawAim.Core.Input;
using DrawAim.Core.Scoring;
using DrawAim.Infrastructure.History;
using DrawAim.Infrastructure.Logging;

namespace DrawAim.App;

public partial class MainWindow
{
    private void GenerateMode2Question()
    {
        _mode2AdvanceCancellation?.Cancel();
        _mode2AdvanceCancellation?.Dispose();
        _mode2AdvanceCancellation = null;
        _mode2FinalCancellation?.Cancel();
        _mode2FinalCancellation?.Dispose();
        _mode2FinalCancellation = null;
        checked
        {
            _mode2QuestionVersion++;
        }
        _mode2PreviewCoordinator.CancelCurrent();
        _mode2Submitting = false;
        _mode2Exercise = null;
        Mode2Canvas.Clear();
        Mode2ReferenceCanvas.TargetCurves = Array.Empty<TargetCurve>();
        Mode2LiveScore.Text = "0%";
        Mode2Status.Text = "可以用任意顺序和方向绘制";
        Mode2ResultHint.Text = "画完后手动提交";

        bool useCountRange = Mode2UseCountRange.IsChecked == true;
        int fixedCount = NormalizeMode2Count(Mode2LineCount.Value);
        int minimumCount = useCountRange
            ? NormalizeMode2Count(Mode2MinCount.Value)
            : fixedCount;
        int maximumCount = useCountRange
            ? NormalizeMode2Count(Mode2MaxCount.Value)
            : fixedCount;
        if (minimumCount > maximumCount)
        {
            (minimumCount, maximumCount) = (maximumCount, minimumCount);
        }

        double minimumLength = NormalizeMode2Percent(Mode2MinLength.Value, 10, 70);
        double maximumLength = NormalizeMode2Percent(Mode2MaxLength.Value, 10, 70);
        if (minimumLength > maximumLength)
        {
            (minimumLength, maximumLength) = (maximumLength, minimumLength);
        }

        double minimumCurvature = NormalizeMode2Percent(Mode2MinCurvature.Value, 0, 45);
        double maximumCurvature = NormalizeMode2Percent(Mode2MaxCurvature.Value, 0, 45);
        if (minimumCurvature > maximumCurvature)
        {
            (minimumCurvature, maximumCurvature) = (maximumCurvature, minimumCurvature);
        }

        var settings = new MultiLineGenerationSettings
        {
            MinimumLineCount = minimumCount,
            MaximumLineCount = maximumCount,
            StraightWeight = NormalizeMode2Weight(Mode2StraightWeight.Value),
            CShapeWeight = NormalizeMode2Weight(Mode2CWeight.Value),
            SShapeWeight = NormalizeMode2Weight(Mode2SWeight.Value),
            MinimumLengthRatio = minimumLength,
            MaximumLengthRatio = maximumLength,
            MinimumCurvatureRatio = minimumCurvature,
            MaximumCurvatureRatio = maximumCurvature,
            SafeMarginRatio = 0.055,
            MinimumSeparationRatio = 0.024,
            AllowIntersections = Mode2AllowIntersections.IsChecked == true,
            Difficulty = (int)Math.Round(Mode2Difficulty.Value),
            MaximumAttemptsPerLine = 128,
        };
        ulong seed = ResolveQuestionSeed(Mode2Seed, Mode2LockSeed, 20260803);
        string questionFingerprint = BuildFingerprint(TrainingModeKind.ObservationCopy);
        var key = new GenerationKey(
            MultiLineGenerator.Version,
            ExerciseMode.CompositionCopy,
            seed,
            _mode2ExerciseIndex,
            questionFingerprint,
            1,
            1);
        GenerationResult<MultiLineExercise> generated = _multiLineGenerator.Generate(key, settings);
        if (!generated.IsSuccess)
        {
            _mode2Exercise = null;
            Mode2Status.Text = $"无法生成组合：{generated.Error?.Message ?? "设置无解"}";
            ApplyInputEnabledState();
            return;
        }

        _mode2Exercise = generated.Value;
        _mode2QuestionFingerprint = questionFingerprint;
        _mode2QuestionSeed = seed;
        Mode2Canvas.StabilizerLevel = (int)Math.Round(Mode2Stability.Value);
        Mode2Canvas.IsInputEnabled = true;
        RenderMode2Reference();
        ApplyInputEnabledState();
    }

    private void RenderMode2Reference()
    {
        if (_mode2Exercise is null || Mode2ReferenceHost.Width < 1)
        {
            return;
        }

        double side = Mode2ReferenceHost.Width;
        Mode2ReferenceCanvas.TargetCurves = _mode2Exercise.Lines
            .Select(curve => new TargetCurve(
                curve.Kind,
                curve.Bezier.Transform(0, Point2.Zero, side),
                Math.Max(0.05, curve.FlatteningTolerance * side),
                curve.SuggestedForward))
            .ToArray();
        Mode2ReferenceCanvas.ShowStartHint = false;
        Mode2ReferenceCanvas.TargetThickness = 2.6;
    }

    private void Mode2NewQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_mode2Submitting)
        {
            Mode2Status.Text = "最终评分正在完成，请稍候。";
            return;
        }

        if (_mode2Exercise is not null)
        {
            TrackHistoryWrite(RecordSkippedAsync(
                TrainingModeKind.ObservationCopy,
                _mode2QuestionSeed,
                _mode2ExerciseIndex,
                MultiLineGenerator.Version,
                _mode2QuestionFingerprint));
            _mode2ExerciseIndex++;
        }

        GenerateMode2Question();
    }

    private void Mode2Canvas_StrokeCompleted(object sender, StrokeCompletedEventArgs e)
    {
        Mode2Status.Text = $"已画 {Mode2Canvas.StrokeCount} 笔；最终评分不按分笔配对";
        ScheduleMode2Preview(Mode2Canvas.GetStrokeSnapshot(), Mode2Canvas.AnswerVersion);
    }

    private void Mode2Canvas_StrokeCancelled(object? sender, EventArgs e)
    {
        Mode2Status.Text = "当前未完成笔迹已由系统取消；已有答案保留。";
        ScheduleMode2Preview(Mode2Canvas.GetStrokeSnapshot(), Mode2Canvas.AnswerVersion);
    }

    private void Mode2Canvas_StrokeUpdated(object? sender, StrokeUpdatedEventArgs e) =>
        ScheduleMode2Preview(e.Snapshot, e.AnswerVersion);

    private void Mode2Canvas_StrokesChanged(object? sender, EventArgs e) =>
        ScheduleMode2Preview(Mode2Canvas.GetStrokeSnapshot(), Mode2Canvas.AnswerVersion);

    private void ScheduleMode2Preview(
        IReadOnlyList<LogicalStroke> snapshot,
        long answerVersion)
    {
        MultiLineExercise? exercise = _mode2Exercise;
        if (exercise is null || _mode2Submitting || _activeRoute != AppRoute.Mode2)
        {
            return;
        }

        IReadOnlyList<LogicalStroke> normalized = NormalizeMode2Strokes(snapshot);
        var key = new LatestWinsKey(
            _mode2QuestionVersion,
            answerVersion,
            SettingsVersion: 0);
        _ = RunMode2PreviewAsync(
            key,
            new Mode2PreviewWork(exercise, normalized));
    }

    private async Task RunMode2PreviewAsync(
        LatestWinsKey key,
        Mode2PreviewWork work)
    {
        try
        {
            LatestWinsExecutionResult<MultiLineScoreResult> execution =
                await _mode2PreviewCoordinator.SubmitAsync(key, work);
            if (execution.Status == LatestWinsStatus.Published &&
                execution.PublishedResult is { } published &&
                key.AnswerVersion == Mode2Canvas.AnswerVersion &&
                ReferenceEquals(work.Exercise, _mode2Exercise) &&
                !_mode2Submitting)
            {
                MultiLineScoreResult result = published.Result;
                Mode2LiveScore.Text = $"{result.Total:F0}%";
                Mode2Status.Text =
                    $"实时：参考覆盖 {result.TargetCoverage:F0}% · 多余几何 {result.ExtraGeometryPercent:F0}%";
            }
        }
        catch (Exception exception)
        {
            await LogSafelyAsync(DrawAimLogLevel.Warning, "模式二预览评分失败。", exception);
        }
    }

    private async void Mode2Submit_Click(object sender, RoutedEventArgs e)
    {
        MultiLineExercise? exercise = _mode2Exercise;
        if (exercise is null || _mode2Submitting)
        {
            return;
        }

        _mode2Submitting = true;
        long submittedVersion = _mode2QuestionVersion;
        int submittedIndex = _mode2ExerciseIndex;
        ulong submittedSeed = _mode2QuestionSeed;
        string submittedFingerprint = _mode2QuestionFingerprint;
        var finalCancellation = new CancellationTokenSource();
        _mode2FinalCancellation?.Cancel();
        _mode2FinalCancellation?.Dispose();
        _mode2FinalCancellation = finalCancellation;
        Mode2Canvas.CancelActiveStroke();
        Mode2Canvas.IsInputEnabled = false;
        _mode2PreviewCoordinator.CancelCurrent();
        IReadOnlyList<LogicalStroke> snapshot = NormalizeMode2Strokes(Mode2Canvas.GetStrokeSnapshot());
        Mode2Status.Text = "答案已冻结，正在进行高精度最终评分…";

        MultiLineScoreResult result;
        try
        {
            result = await Task.Run(() => MultiLineScoreV1.Score(
                exercise.Lines,
                snapshot,
                toleranceNormalized: 0.022,
                gridResolution: 512,
                finalCancellation.Token),
                finalCancellation.Token);
        }
        catch (OperationCanceledException) when (finalCancellation.IsCancellationRequested)
        {
            if (submittedVersion == _mode2QuestionVersion)
            {
                _mode2Submitting = false;
                ApplyInputEnabledState();
            }

            return;
        }
        catch (Exception exception)
        {
            _mode2Submitting = false;
            Mode2Status.Text = "评分失败，答案仍保留，可以再次提交。";
            ApplyInputEnabledState();
            await LogSafelyAsync(DrawAimLogLevel.Error, "模式二最终评分失败。", exception);
            return;
        }
        finally
        {
            if (ReferenceEquals(_mode2FinalCancellation, finalCancellation))
            {
                _mode2FinalCancellation = null;
            }

            finalCancellation.Dispose();
        }

        if (submittedVersion != _mode2QuestionVersion ||
            !ReferenceEquals(exercise, _mode2Exercise))
        {
            return;
        }

        ShowMode2Result(result);
        int stability = snapshot.Count == 0
            ? (int)Math.Round(Mode2Stability.Value)
            : snapshot.Max(static stroke => stroke.StabilizerLevel);
        string stabilizerVersion = snapshot.Count == 0
            ? StrokeStabilizerV1.Version
            : string.Join(
                "+",
                snapshot
                    .Select(static stroke => stroke.StabilizerVersion)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));
        TrackHistoryWrite(RecordCompletedAsync(
            TrainingModeKind.ObservationCopy,
            result.Total,
            stability,
            submittedSeed,
            submittedIndex,
            MultiLineGenerator.Version,
            MultiLineScoreV1.Version,
            new Dictionary<string, double>
            {
                ["TargetCoverage"] = result.TargetCoverage,
                ["UserPrecision"] = result.UserPrecision,
                ["LayoutSimilarity"] = result.LayoutSimilarity,
                ["ExtraGeometry"] = result.ExtraGeometryPercent,
            },
            submittedFingerprint,
            stabilizerVersion));

        var advanceCancellation = new CancellationTokenSource();
        _mode2AdvanceCancellation?.Cancel();
        _mode2AdvanceCancellation?.Dispose();
        _mode2AdvanceCancellation = advanceCancellation;
        try
        {
            await DelayWhileUnpausedAsync(
                TimeSpan.FromMilliseconds(900),
                advanceCancellation.Token);

            if (_activeRoute == AppRoute.Mode2 &&
                _mode2ExerciseIndex == submittedIndex &&
                _mode2QuestionVersion == submittedVersion &&
                ReferenceEquals(exercise, _mode2Exercise))
            {
                if (ReferenceEquals(_mode2AdvanceCancellation, advanceCancellation))
                {
                    _mode2AdvanceCancellation = null;
                }

                _mode2ExerciseIndex++;
                GenerateMode2Question();
            }
            else if (_mode2ExerciseIndex == submittedIndex &&
                     _mode2QuestionVersion == submittedVersion &&
                     ReferenceEquals(exercise, _mode2Exercise))
            {
                _mode2ExerciseIndex++;
                _mode2Exercise = null;
                _mode2Submitting = false;
                Mode2Canvas.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Closing or a replacement question superseded the result delay.
        }
        finally
        {
            if (ReferenceEquals(_mode2AdvanceCancellation, advanceCancellation))
            {
                _mode2AdvanceCancellation = null;
            }

            advanceCancellation.Dispose();
        }
    }

    private void ShowMode2Result(MultiLineScoreResult result)
    {
        Mode2LiveScore.Text = $"{result.Total:F0}%";
        Mode2TotalScore.Text = $"{result.Total:F1}";
        Mode2Coverage.Text = $"{result.TargetCoverage:F1}%";
        Mode2Precision.Text = $"{result.UserPrecision:F1}%";
        Mode2Extra.Text = $"{result.ExtraGeometryPercent:F1}%";
        Mode2Position.Text = $"{result.PositionErrorNormalized * 100:F1}%";
        Mode2LengthError.Text = $"{result.LengthErrorRatio * 100:+0.0;-0.0;0.0}%";
        Mode2ResultHint.Text = result.Total switch
        {
            >= 90 => "位置与结构都很接近",
            >= 70 => "检查最明显的漏画或错位",
            _ => "优先匹配相对位置和线条数量",
        };
        Mode2Status.Text =
            $"已提交 · 方向误差 {result.DirectionErrorDegrees:F1}° · 曲率误差 {result.CurvatureError:F2}";
    }

    private IReadOnlyList<LogicalStroke> NormalizeMode2Strokes(
        IReadOnlyList<LogicalStroke> strokes)
    {
        double width = Math.Max(1, Mode2Canvas.ActualWidth);
        double height = Math.Max(1, Mode2Canvas.ActualHeight);
        return strokes.Select(stroke => new LogicalStroke(
                stroke.Samples.Select(sample => new StrokeSample(
                    new Point2(sample.Position.X / width, sample.Position.Y / height),
                    sample.TimestampSeconds,
                    sample.Pressure)),
                stroke.StabilizerLevel,
                stroke.StabilizerVersion))
            .ToArray();
    }

    private void Mode2Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_mode2Submitting)
        {
            return;
        }

        if (Mode2Canvas.Undo())
        {
            Mode2Status.Text = "已撤销";
            ScheduleMode2Preview(Mode2Canvas.GetStrokeSnapshot(), Mode2Canvas.AnswerVersion);
        }
    }

    private void Mode2Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_mode2Submitting)
        {
            return;
        }

        if (Mode2Canvas.Redo())
        {
            Mode2Status.Text = "已重做";
            ScheduleMode2Preview(Mode2Canvas.GetStrokeSnapshot(), Mode2Canvas.AnswerVersion);
        }
    }

    private void Mode2Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_mode2Submitting)
        {
            return;
        }

        Mode2Canvas.Clear();
        Mode2LiveScore.Text = "0%";
        Mode2Status.Text = "答案已清空";
        ScheduleMode2Preview(Mode2Canvas.GetStrokeSnapshot(), Mode2Canvas.AnswerVersion);
    }

    private void Mode2Settings_Changed(object sender, EventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        NormalizeMode2RangeControls(sender);

        bool useCountRange = Mode2UseCountRange.IsChecked == true;
        Mode2LineCount.IsEnabled = !useCountRange;
        Mode2MinCount.IsEnabled = useCountRange;
        Mode2MaxCount.IsEnabled = useCountRange;

        Mode2CountText.Text = NormalizeMode2Count(Mode2LineCount.Value)
            .ToString(CultureInfo.InvariantCulture);
        Mode2MinCountText.Text = NormalizeMode2Count(Mode2MinCount.Value)
            .ToString(CultureInfo.InvariantCulture);
        Mode2MaxCountText.Text = NormalizeMode2Count(Mode2MaxCount.Value)
            .ToString(CultureInfo.InvariantCulture);
        Mode2StraightWeightText.Text = $"{NormalizeMode2Weight(Mode2StraightWeight.Value):F0}";
        Mode2CWeightText.Text = $"{NormalizeMode2Weight(Mode2CWeight.Value):F0}";
        Mode2SWeightText.Text = $"{NormalizeMode2Weight(Mode2SWeight.Value):F0}";
        Mode2DifficultyText.Text = Math.Round(Mode2Difficulty.Value).ToString(CultureInfo.InvariantCulture);
        Mode2MinLengthText.Text = $"{Math.Clamp(Mode2MinLength.Value, 10, 70):F0}%";
        Mode2MaxLengthText.Text = $"{Math.Clamp(Mode2MaxLength.Value, 10, 70):F0}%";
        Mode2MinCurvatureText.Text = $"{Math.Clamp(Mode2MinCurvature.Value, 0, 45):F0}%";
        Mode2MaxCurvatureText.Text = $"{Math.Clamp(Mode2MaxCurvature.Value, 0, 45):F0}%";
    }

    private void NormalizeMode2RangeControls(object sender)
    {
        if (Mode2MinCount.Value > Mode2MaxCount.Value)
        {
            if (ReferenceEquals(sender, Mode2MaxCount))
            {
                Mode2MinCount.Value = Mode2MaxCount.Value;
            }
            else
            {
                Mode2MaxCount.Value = Mode2MinCount.Value;
            }
        }

        if (Mode2MinLength.Value > Mode2MaxLength.Value)
        {
            if (ReferenceEquals(sender, Mode2MaxLength))
            {
                Mode2MinLength.Value = Mode2MaxLength.Value;
            }
            else
            {
                Mode2MaxLength.Value = Mode2MinLength.Value;
            }
        }

        if (Mode2MinCurvature.Value > Mode2MaxCurvature.Value)
        {
            if (ReferenceEquals(sender, Mode2MaxCurvature))
            {
                Mode2MinCurvature.Value = Mode2MaxCurvature.Value;
            }
            else
            {
                Mode2MaxCurvature.Value = Mode2MinCurvature.Value;
            }
        }
    }

    private static int NormalizeMode2Count(double value) =>
        double.IsFinite(value)
            ? Math.Clamp((int)Math.Round(value), 1, 10)
            : 1;

    private static double NormalizeMode2Weight(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    private static double NormalizeMode2Percent(double value, double minimum, double maximum) =>
        (double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum) / 100.0;

    private void Mode2Stability_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        int level = (int)Math.Round(e.NewValue);
        Mode2StabilityText.Text = StabilityLabel(level);
        Mode2Canvas.StabilizerLevel = level;
        Mode2Status.Text = Mode2Canvas.StrokeCount == 0
            ? "稳定值已更新"
            : "稳定值从下一笔生效；最终只比较可见几何";
    }

    private void Mode2SquareArea_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ResizeMode2Squares();

    private void ResizeMode2Squares()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        double availableWidth = Mode2SquareArea.ActualWidth;
        double availableHeight = Mode2SquareArea.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        double side = Math.Floor(Math.Min(
            Math.Max(180, (availableWidth - 34) / 2),
            Math.Max(180, availableHeight - 38)));
        side = Math.Clamp(side, 180, 720);
        Mode2ReferenceHost.Width = side;
        Mode2ReferenceHost.Height = side;
        Mode2AnswerHost.Width = side;
        Mode2AnswerHost.Height = side;
        RenderMode2Reference();
    }
}
