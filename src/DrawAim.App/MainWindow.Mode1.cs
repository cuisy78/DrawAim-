using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DrawAim.App.Controls;
using DrawAim.Core.Generation;
using DrawAim.Core.Geometry;
using DrawAim.Core.Scoring;
using DrawAim.Infrastructure.History;

namespace DrawAim.App;

public partial class MainWindow
{
    private void GenerateMode1Question(bool preserveQuestionSeed = false)
    {
        _mode1AdvanceCancellation?.Cancel();
        _mode1LiveCancellation?.Cancel();
        checked
        {
            _mode1QuestionVersion++;
        }
        _mode1QuestionActive = false;
        _mode1Target = null;
        Mode1Canvas.IsInputEnabled = false;
        Mode1Canvas.Clear();
        Mode1Canvas.TargetCurves = Array.Empty<TargetCurve>();

        double width = Mode1Canvas.ActualWidth >= 200 ? Mode1Canvas.ActualWidth : 780;
        double height = Mode1Canvas.ActualHeight >= 200 ? Mode1Canvas.ActualHeight : 580;
        double length = Mode1Length.Value / 100.0;
        double curvature = Mode1Curvature.Value / 100.0;
        (double straight, double cShape, double sShape) = GetMode1LineWeights();
        (double minimumDirection, double maximumDirection) = NormalizeOrderedRange(
            Mode1DirectionMin.Value,
            Mode1DirectionMax.Value,
            0,
            360,
            1);

        var settings = new LineGenerationSettings
        {
            StraightWeight = straight,
            CShapeWeight = cShape,
            SShapeWeight = sShape,
            MinimumLengthRatio = Math.Clamp(length - 0.055, 0.15, 0.86),
            MaximumLengthRatio = Math.Clamp(length + 0.055, 0.16, 0.88),
            MinimumCurvatureRatio = Math.Clamp(curvature - 0.035, 0.01, 0.38),
            MaximumCurvatureRatio = Math.Clamp(curvature + 0.035, 0.02, 0.42),
            MinimumDirectionDegrees = minimumDirection,
            MaximumDirectionDegrees = maximumDirection,
            SafeMarginRatio = 0.075,
            Difficulty = (int)Math.Round(Mode1Difficulty.Value),
            MaximumAttempts = 80,
        };
        ulong seed = preserveQuestionSeed
            ? _mode1QuestionSeed
            : ResolveQuestionSeed(Mode1Seed, Mode1LockSeed, 20260803);
        string questionFingerprint = BuildFingerprint(
            TrainingModeKind.LineFollow,
            width,
            height);
        double questionTolerance = Mode1Tolerance.Value;
        HomeSeedText.Text = seed.ToString(CultureInfo.InvariantCulture);
        var key = new GenerationKey(
            TargetLineGenerator.Version,
            ExerciseMode.LineTrace,
            seed,
            _mode1ExerciseIndex,
            questionFingerprint,
            width,
            height);
        GenerationResult<TargetCurve> generated = _lineGenerator.Generate(key, settings);
        if (!generated.IsSuccess)
        {
            _mode1Target = null;
            Mode1Instruction.Text = $"无法生成题目：{generated.Error?.Message ?? "设置无解"}";
            Mode1LiveFeedback.Text = "请调整长度、曲率或画布尺寸";
            ApplyInputEnabledState();
            return;
        }

        _mode1Target = generated.Value;
        _mode1QuestionFingerprint = questionFingerprint;
        _mode1QuestionSeed = seed;
        _mode1QuestionTolerance = questionTolerance;
        _mode1QuestionCanvasWidth = width;
        _mode1QuestionCanvasHeight = height;
        Mode1Canvas.TargetCurves = new[] { generated.Value };
        Mode1Canvas.ShowStartHint = Mode1ShowHint.IsChecked == true;
        Mode1Canvas.TargetThickness = Mode1TargetWidth.Value;
        Mode1Canvas.BrushSize = Mode1AnswerWidth.Value;
        Mode1Canvas.StabilizerLevel = (int)Math.Round(Mode1Stability.Value);
        _mode1QuestionActive = true;
        Mode1Instruction.Text = "画布内任意位置落笔；抬笔后自动提交。";
        Mode1LiveFeedback.Text = generated.UsedFallback ? "已使用确定性安全题目" : "准备作答";
        Mode1ResultHint.Text = $"第 {_mode1ExerciseIndex + 1} 题 · 抬笔后显示结果";
        ApplyInputEnabledState();
    }

    private void Mode1NewQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_mode1Target is not null)
        {
            if (_mode1QuestionActive)
            {
                TrackHistoryWrite(RecordSkippedAsync(
                    TrainingModeKind.LineFollow,
                    _mode1QuestionSeed,
                    _mode1ExerciseIndex,
                    TargetLineGenerator.Version,
                    _mode1QuestionFingerprint));
            }

            _mode1ExerciseIndex++;
        }

        GenerateMode1Question();
    }

    private void Mode1Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_mode1Target is not null && _mode1QuestionActive)
        {
            TrackHistoryWrite(RecordSkippedAsync(
                TrainingModeKind.LineFollow,
                _mode1QuestionSeed,
                _mode1ExerciseIndex,
                TargetLineGenerator.Version,
                _mode1QuestionFingerprint));
        }

        _mode1ExerciseIndex++;
        GenerateMode1Question();
    }

    private void Mode1Canvas_StrokeStarted(object? sender, EventArgs e)
    {
        if (!_mode1QuestionActive)
        {
            return;
        }

        Mode1Instruction.Text = "正在作答；第一次抬笔就是最终答案。";
        Mode1LiveFeedback.Text = "计算实时覆盖…";
    }

    private void Mode1Canvas_StrokeCancelled(object? sender, EventArgs e)
    {
        if (_mode1QuestionActive)
        {
            Mode1Instruction.Text = "本笔由系统取消，未提交；可以重新落笔。";
            Mode1LiveFeedback.Text = "系统取消不计分";
        }
    }

    private async void Mode1Canvas_StrokeUpdated(object? sender, StrokeUpdatedEventArgs e)
    {
        TargetCurve? target = _mode1Target;
        if (!_mode1QuestionActive || target is null || e.Snapshot.Count == 0)
        {
            return;
        }

        LogicalStroke answer = e.Snapshot[^1];
        double tolerance = _mode1QuestionTolerance;
        _mode1LiveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _mode1LiveCancellation = cancellation;
        try
        {
            LineScoreResult preview = await Task.Run(
                () => LineScoreV1.Score(target, answer, tolerance),
                cancellation.Token);
            if (!cancellation.IsCancellationRequested &&
                _mode1QuestionActive &&
                ReferenceEquals(target, _mode1Target))
            {
                Mode1LiveFeedback.Text =
                    $"实时覆盖 {preview.Coverage:F0}%  ·  平均偏离 {preview.MeanDistance:F1} DIP";
            }
        }
        catch (OperationCanceledException)
        {
            // A newer immutable snapshot superseded this preview.
        }
    }

    private async void Mode1Canvas_StrokeCompleted(object sender, StrokeCompletedEventArgs e)
    {
        TargetCurve? target = _mode1Target;
        if (!_mode1QuestionActive || target is null)
        {
            return;
        }

        _mode1QuestionActive = false;
        long submittedVersion = _mode1QuestionVersion;
        int submittedIndex = _mode1ExerciseIndex;
        ulong submittedSeed = _mode1QuestionSeed;
        string submittedFingerprint = _mode1QuestionFingerprint;
        _mode1LiveCancellation?.Cancel();
        Mode1Canvas.IsInputEnabled = false;
        Mode1Instruction.Text = "答案已冻结，正在评分…";

        double tolerance = _mode1QuestionTolerance;
        LineScoreResult result;
        try
        {
            result = await Task.Run(() => LineScoreV1.Score(target, e.Stroke, tolerance));
        }
        catch (Exception exception)
        {
            if (submittedVersion != _mode1QuestionVersion ||
                !ReferenceEquals(target, _mode1Target))
            {
                await LogSafelyAsync(Infrastructure.Logging.DrawAimLogLevel.Error, "过期的模式一评分失败。", exception);
                return;
            }

            Mode1Instruction.Text = "评分失败，本题未计入成绩。";
            await LogSafelyAsync(Infrastructure.Logging.DrawAimLogLevel.Error, "模式一评分失败。", exception);
            _mode1ExerciseIndex++;
            GenerateMode1Question();
            return;
        }

        TrackHistoryWrite(RecordCompletedAsync(
            TrainingModeKind.LineFollow,
            result.Total,
            e.Stroke.StabilizerLevel,
            submittedSeed,
            submittedIndex,
            TargetLineGenerator.Version,
            LineScoreV1.Version,
            new Dictionary<string, double>
            {
                ["Accuracy"] = result.Accuracy,
                ["Coverage"] = result.Coverage,
                ["Smoothness"] = result.Smoothness,
                ["Economy"] = result.Economy,
            },
            submittedFingerprint,
            e.Stroke.StabilizerVersion));

        if (submittedVersion != _mode1QuestionVersion ||
            !ReferenceEquals(target, _mode1Target))
        {
            return;
        }

        ShowMode1Result(result, e.Stroke.StabilizerLevel);
        _mode1AdvanceCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _mode1AdvanceCancellation = cancellation;
        try
        {
            await DelayWhileUnpausedAsync(TimeSpan.FromMilliseconds(650), cancellation.Token);

            if (!cancellation.IsCancellationRequested &&
                _activeRoute == AppRoute.Mode1 &&
                _mode1QuestionVersion == submittedVersion &&
                _mode1ExerciseIndex == submittedIndex)
            {
                _mode1ExerciseIndex = submittedIndex + 1;
                GenerateMode1Question();
            }
            else if (!cancellation.IsCancellationRequested &&
                     _mode1QuestionVersion == submittedVersion &&
                     _mode1ExerciseIndex == submittedIndex)
            {
                _mode1ExerciseIndex = submittedIndex + 1;
                _mode1Target = null;
                Mode1Canvas.TargetCurves = Array.Empty<TargetCurve>();
            }
        }
        catch (OperationCanceledException)
        {
            // The user changed page or requested another question.
        }
    }

    private void ShowMode1Result(LineScoreResult result, int stability)
    {
        Mode1TotalScore.Text = $"{result.Total:F1}";
        Mode1Accuracy.Text = $"{result.Accuracy:F1}";
        Mode1Coverage.Text = $"{result.Coverage:F1}";
        Mode1Smoothness.Text = $"{result.Smoothness:F1}";
        Mode1Economy.Text = $"{result.Economy:F1}";
        Mode1AccuracyBar.Value = result.Accuracy;
        Mode1CoverageBar.Value = result.Coverage;
        Mode1SmoothnessBar.Value = result.Smoothness;
        Mode1EconomyBar.Value = result.Economy;
        Mode1ResultHint.Text = stability == 0
            ? "无稳定辅助成绩"
            : $"稳定辅助 {stability} · 与无辅助成绩分开";
        Mode1Instruction.Text = "已自动提交；下一题即将就绪。";
        Mode1LiveFeedback.Text = result.Total switch
        {
            >= 90 => "控制很稳，继续保持",
            >= 70 => "整体不错，注意最偏离的局部",
            >= 45 => "先优先完成整条目标线",
            _ => "覆盖不足或偏离较多，下一题放慢一些",
        };
    }

    private void Mode1Settings_Changed(object sender, EventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        NormalizeMode1DirectionControls(sender);
        Mode1DifficultyText.Text = Math.Round(Mode1Difficulty.Value).ToString(CultureInfo.InvariantCulture);
        Mode1LengthText.Text = $"{Mode1Length.Value:F0}%";
        Mode1CurvatureText.Text = $"{Mode1Curvature.Value:F0}%";
        Mode1StraightWeightText.Text = $"{Mode1StraightWeight.Value:F0}";
        Mode1CWeightText.Text = $"{Mode1CWeight.Value:F0}";
        Mode1SWeightText.Text = $"{Mode1SWeight.Value:F0}";
        bool mixed = Mode1LineKind.SelectedIndex == 3;
        Mode1StraightWeight.IsEnabled = mixed;
        Mode1CWeight.IsEnabled = mixed;
        Mode1SWeight.IsEnabled = mixed;
        (double minimumDirection, double maximumDirection) = NormalizeOrderedRange(
            Mode1DirectionMin.Value,
            Mode1DirectionMax.Value,
            0,
            360,
            1);
        Mode1DirectionMinText.Text = $"{minimumDirection:F0}°";
        Mode1DirectionMaxText.Text = $"{maximumDirection:F0}°";
        Mode1AnswerWidthText.Text = $"{Mode1AnswerWidth.Value:F1} DIP";
        Mode1TargetWidthText.Text = $"{Mode1TargetWidth.Value:F1} DIP";
        Mode1ToleranceText.Text = $"{Mode1Tolerance.Value:F0} DIP";
        Mode1Canvas.BrushSize = Mode1AnswerWidth.Value;
        if (!_mode1QuestionActive && Mode1Canvas is not null)
        {
            Mode1Canvas.ShowStartHint = Mode1ShowHint.IsChecked == true;
            Mode1Canvas.TargetThickness = Mode1TargetWidth.Value;
        }
        else if (_mode1QuestionActive && ReferenceEquals(sender, Mode1AnswerWidth))
        {
            Mode1Instruction.Text = "你的作答笔宽已更新；评分仍只看中心轨迹。";
        }
        else if (_mode1QuestionActive)
        {
            Mode1Instruction.Text = "题目设置已更新；点击生成新题后生效。";
        }
    }

    private (double Straight, double CShape, double SShape) GetMode1LineWeights() =>
        Mode1LineKind.SelectedIndex switch
        {
            0 => (1, 0, 0),
            1 => (0, 1, 0),
            2 => (0, 0, 1),
            _ => (
                Mode1StraightWeight.Value,
                Mode1CWeight.Value,
                Mode1SWeight.Value),
        };

    private void NormalizeMode1DirectionControls(object sender)
    {
        if (Mode1DirectionMin.Value < Mode1DirectionMax.Value)
        {
            return;
        }

        if (ReferenceEquals(sender, Mode1DirectionMax))
        {
            Mode1DirectionMin.Value = Math.Max(
                Mode1DirectionMin.Minimum,
                Mode1DirectionMax.Value - 1);
        }
        else
        {
            Mode1DirectionMax.Value = Math.Min(
                Mode1DirectionMax.Maximum,
                Mode1DirectionMin.Value + 1);
        }
    }

    private async void ScheduleMode1CanvasRefresh()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _mode1ResizeCancellation?.Cancel();
        _mode1ResizeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _mode1ResizeCancellation = cancellation;
        try
        {
            await Task.Delay(140, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosing)
            {
                return;
            }

            if (_activeRoute != AppRoute.Mode1)
            {
                if (_mode1Target is not null && _mode1QuestionActive)
                {
                    _mode1QuestionActive = false;
                    _mode1Target = null;
                    Mode1Canvas.TargetCurves = Array.Empty<TargetCurve>();
                }

                return;
            }

            double width = Mode1Canvas.ActualWidth;
            double height = Mode1Canvas.ActualHeight;
            if (_mode1QuestionActive && _mode1Target is not null &&
                width >= 200 && height >= 200 &&
                (Math.Abs(width - _mode1QuestionCanvasWidth) > 1 ||
                 Math.Abs(height - _mode1QuestionCanvasHeight) > 1))
            {
                GenerateMode1Question(preserveQuestionSeed: true);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer resize superseded this refresh.
        }
        finally
        {
            if (ReferenceEquals(_mode1ResizeCancellation, cancellation))
            {
                _mode1ResizeCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void Mode1Stability_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        int level = (int)Math.Round(e.NewValue);
        Mode1StabilityText.Text = StabilityLabel(level);
        if (_mode1QuestionActive)
        {
            Mode1Instruction.Text = $"稳定 {level} 将从下一题生效；当前题仍使用已锁定值。";
        }
    }

    private void Mode1StabilityPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            int.TryParse(value, CultureInfo.InvariantCulture, out int level))
        {
            Mode1Stability.Value = level;
        }
    }
}
