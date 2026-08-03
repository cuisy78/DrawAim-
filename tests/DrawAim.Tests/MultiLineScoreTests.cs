using DrawAim.Core.Geometry;
using DrawAim.Core.Generation;
using DrawAim.Core.Scoring;
using System.Diagnostics;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> MultiLineScoreTests()
    {
        yield return new TestCase("MultiLineScore/完美答案接近 100", PerfectCompositionScoresNearOneHundred);
        yield return new TestCase("MultiLineScore/笔画顺序不变", StrokeOrderDoesNotMatter);
        yield return new TestCase("MultiLineScore/绘制方向不变", StrokeDirectionDoesNotMatter);
        yield return new TestCase("MultiLineScore/采样密度基本不变", CompositionSamplingDensityDoesNotMatter);
        yield return new TestCase("MultiLineScore/拆笔合笔不变", SplittingAStrokeDoesNotMatter);
        yield return new TestCase("MultiLineScore/重复描正确几何不加权", RepeatedCorrectGeometryHasNoExtraWeight);
        yield return new TestCase("MultiLineScore/重复正确线不能稀释错误线", RepetitionCannotDiluteExtraGeometry);
        yield return new TestCase("MultiLineScore/整体错位明显扣分", GlobalOffsetIsPenalized);
        yield return new TestCase("MultiLineScore/漏画与多画分别扣分", MissingAndExtraGeometryArePenalized);
        yield return new TestCase("MultiLineScore/已取消任务立即停止", CancelledScoringStops);
        yield return new TestCase("MultiLineScore/画布外超长线段先裁剪", HugeOutsideSegmentIsClippedBeforeRasterization);
        yield return new TestCase("MultiLineScore/运行中取消及时停止栅格化", InFlightCancellationStopsRasterization);
        yield return new TestCase("MultiLineScore/10 线 512 网格性能预算", TenLinePreviewMeetsPerformanceBudget);
    }

    private static void PerfectCompositionScoresNearOneHundred()
    {
        var target = CompositionTargets();
        var result = ScoreComposition(target, ExactAnswer(target));
        AssertEx.InRange(result.Total, 99, 100, $"完美组合总分仅 {result.Total:F3}。" );
        AssertEx.InRange(result.TargetCoverage, 99.9, 100);
        AssertEx.InRange(result.UserPrecision, 99.9, 100);
        AssertEx.InRange(result.LayoutSimilarity, 99.9, 100);
    }

    private static void StrokeOrderDoesNotMatter()
    {
        var target = CompositionTargets();
        var forward = ScoreComposition(target, ExactAnswer(target));
        var reversedAnswer = ExactAnswer(target).Reverse().ToArray();
        var reverse = ScoreComposition(target, reversedAnswer);
        AssertEx.Near(forward.Total, reverse.Total, 0.01);
        AssertEx.Near(forward.UserPrecision, reverse.UserPrecision, 0.01);
    }

    private static void StrokeDirectionDoesNotMatter()
    {
        var target = CompositionTargets();
        var forwardAnswer = ExactAnswer(target);
        var forward = ScoreComposition(target, forwardAnswer);
        var reversed = forwardAnswer
            .Select(stroke => new LogicalStroke(
                stroke.Samples
                    .Reverse()
                    .Select((sample, index) => new StrokeSample(
                        sample.Position,
                        index / 120.0,
                        sample.Pressure))))
            .ToArray();
        var result = ScoreComposition(target, reversed);
        AssertEx.Near(forward.Total, result.Total, 1e-9);
    }

    private static void CompositionSamplingDensityDoesNotMatter()
    {
        var target = CompositionTargets();
        var sparse = target.Select(curve => StrokeFromPoints(curve.Polyline, 1)).ToArray();
        var dense = target.Select(curve => StrokeFromPoints(curve.Polyline, 8)).ToArray();
        var sparseScore = ScoreComposition(target, sparse);
        var denseScore = ScoreComposition(target, dense);
        AssertEx.Near(sparseScore.Total, denseScore.Total, 0.5);
        AssertEx.Near(sparseScore.UserPrecision, denseScore.UserPrecision, 0.5);
    }

    private static void SplittingAStrokeDoesNotMatter()
    {
        var target = new[]
        {
            StraightTarget(new Point2(0.1, 0.5), new Point2(0.9, 0.5)),
        };
        var start = target[0].Bezier.P0;
        var end = target[0].Bezier.P3;
        var midpoint = Point2.Lerp(start, end, 0.5);
        var oneStroke = ScoreComposition(target, [StrokeFromPoints([start, midpoint, end], 8)]);
        var split = ScoreComposition(
            target,
            [StrokeFromPoints([start, midpoint], 8), StrokeFromPoints([midpoint, end], 8)]);
        AssertEx.Near(oneStroke.Total, split.Total, 0.2);
        AssertEx.Near(oneStroke.UserPrecision, split.UserPrecision, 0.2);
    }

    private static void RepeatedCorrectGeometryHasNoExtraWeight()
    {
        var target = CompositionTargets();
        var single = ExactAnswer(target);
        var repeated = new List<LogicalStroke>();
        for (var repeat = 0; repeat < 20; repeat++)
        {
            repeated.AddRange(ExactAnswer(target));
        }

        var first = ScoreComposition(target, single);
        var second = ScoreComposition(target, repeated);
        AssertEx.Near(first.Total, second.Total, 0.001);
        AssertEx.Near(first.UserPrecision, second.UserPrecision, 0.001);
        AssertEx.Near(first.ExtraGeometryPercent, second.ExtraGeometryPercent, 0.001);
    }

    private static void RepetitionCannotDiluteExtraGeometry()
    {
        var target = new[]
        {
            StraightTarget(new Point2(0.1, 0.25), new Point2(0.9, 0.25)),
        };
        var correct = StrokeFromPoints(target[0].Polyline, 8);
        var wrong = StrokeFromPoints([new Point2(0.1, 0.8), new Point2(0.9, 0.8)], 8);
        var once = ScoreComposition(target, [correct, wrong]);
        var repeated = new List<LogicalStroke> { wrong };
        for (var index = 0; index < 30; index++)
        {
            repeated.Add(correct);
        }

        var many = ScoreComposition(target, repeated);
        AssertEx.Near(once.Total, many.Total, 0.001);
        AssertEx.Near(once.UserPrecision, many.UserPrecision, 0.001);
        AssertEx.Near(once.ExtraGeometryPercent, many.ExtraGeometryPercent, 0.001);
        AssertEx.True(many.UserPrecision < 70, "错误线被大量重复正确线稀释。" );
    }

    private static void GlobalOffsetIsPenalized()
    {
        var target = CompositionTargets();
        var perfect = ScoreComposition(target, ExactAnswer(target));
        var shifted = target
            .Select(curve => StrokeFromPoints(Offset(curve.Polyline, new Point2(0.12, 0.10))))
            .ToArray();
        var result = ScoreComposition(target, shifted);
        AssertEx.True(result.Total < perfect.Total - 45, $"整体错位后仍有 {result.Total:F2} 分。" );
        AssertEx.True(result.PositionErrorNormalized > 0.12, "位置误差诊断过小。" );
    }

    private static void MissingAndExtraGeometryArePenalized()
    {
        var target = CompositionTargets();
        var perfect = ScoreComposition(target, ExactAnswer(target));
        var missing = ScoreComposition(target, [StrokeFromPoints(target[0].Polyline)]);
        var extraAnswer = ExactAnswer(target).ToList();
        extraAnswer.Add(StrokeFromPoints([new Point2(0.1, 0.92), new Point2(0.9, 0.92)]));
        var extra = ScoreComposition(target, extraAnswer);
        AssertEx.True(missing.Total < perfect.Total - 15, "漏画一根没有明显扣分。" );
        AssertEx.True(missing.MissingGeometryPercent > 10, "漏画诊断没有报告缺失。" );
        AssertEx.True(extra.Total < perfect.Total - 10, "多画一根没有明显扣分。" );
        AssertEx.True(extra.ExtraGeometryPercent > 10, "多画诊断没有报告多余几何。" );
    }

    private static void CancelledScoringStops()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() =>
            MultiLineScoreV1.Score(
                CompositionTargets(),
                ExactAnswer(CompositionTargets()),
                0.02,
                256,
                cancellation.Token));
    }

    private static void HugeOutsideSegmentIsClippedBeforeRasterization()
    {
        var target = new[]
        {
            StraightTarget(new Point2(0.10, 0.50), new Point2(0.90, 0.50)),
        };
        var answer = new[]
        {
            StrokeFromPoints([new Point2(-100_000, 0.50), new Point2(100_000, 0.50)]),
        };
        var timer = Stopwatch.StartNew();
        var result = MultiLineScoreV1.Score(target, answer, 0.02, 512);
        timer.Stop();

        AssertFinite(result.Total, result.TargetCoverage, result.UserPrecision);
        AssertEx.InRange(
            timer.Elapsed.TotalMilliseconds,
            0,
            500,
            $"画布外超长线段耗时 {timer.Elapsed.TotalMilliseconds:F1} ms，疑似在裁剪前按原长度栅格化。" );
    }

    private static void InFlightCancellationStopsRasterization()
    {
        const int pointCount = 250_000;
        var samples = new StrokeSample[pointCount];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = new StrokeSample(
                (index & 1) == 0 ? new Point2(0, 0) : new Point2(1, 1),
                index / 240.0,
                1);
        }

        var answer = new[] { new LogicalStroke(samples) };
        using var cancellation = new CancellationTokenSource();
        var cancellationThread = new Thread(() =>
        {
            Thread.Sleep(20);
            cancellation.Cancel();
        })
        {
            IsBackground = true,
        };
        cancellationThread.Start();
        AssertEx.False(cancellation.IsCancellationRequested, "评分调用前令牌已经取消。" );
        var timer = Stopwatch.StartNew();
        AssertEx.Throws<OperationCanceledException>(() =>
            MultiLineScoreV1.Score(
                CompositionTargets(),
                answer,
                0.02,
                64,
                cancellation.Token));
        timer.Stop();
        cancellationThread.Join();
        AssertEx.InRange(
            timer.Elapsed.TotalMilliseconds,
            0,
            1_000,
            $"运行中取消耗时 {timer.Elapsed.TotalMilliseconds:F1} ms。" );
    }

    private static void TenLinePreviewMeetsPerformanceBudget()
    {
        var generated = new MultiLineGenerator().Generate(
            new GenerationKey(
                MultiLineGenerator.Version,
                ExerciseMode.CompositionCopy,
                0xBEEF,
                3,
                "performance",
                1,
                1),
            MultiLineGenerationSettings.Default with
            {
                MinimumLineCount = 10,
                MaximumLineCount = 10,
                MinimumLengthRatio = 0.12,
                MaximumLengthRatio = 0.30,
            });
        AssertEx.True(generated.IsSuccess, $"性能基准组合生成失败：{generated.Error?.Message}" );
        var answer = ExactAnswer(generated.Value.Lines);
        _ = MultiLineScoreV1.Score(generated.Value.Lines, answer, 0.02, 512);

        var timings = new double[7];
        for (var index = 0; index < timings.Length; index++)
        {
            var timer = Stopwatch.StartNew();
            _ = MultiLineScoreV1.Score(generated.Value.Lines, answer, 0.02, 512);
            timings[index] = timer.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        var p95 = timings[^1];
        AssertEx.InRange(p95, 0, 80, $"10 线 512 网格预览 P95 近似值为 {p95:F2} ms。" );
    }

    private static IReadOnlyList<TargetCurve> CompositionTargets() =>
    [
        StraightTarget(new Point2(0.10, 0.22), new Point2(0.82, 0.22)),
        new TargetCurve(
            CurveKind.CShape,
            new CubicBezier2(
                new Point2(0.18, 0.72),
                new Point2(0.30, 0.42),
                new Point2(0.62, 0.42),
                new Point2(0.78, 0.72)),
            0.0005),
    ];

    private static IReadOnlyList<LogicalStroke> ExactAnswer(IReadOnlyList<TargetCurve> target) =>
        target.Select(curve => StrokeFromPoints(curve.Polyline, 2)).ToArray();

    private static MultiLineScoreResult ScoreComposition(
        IReadOnlyList<TargetCurve> target,
        IReadOnlyList<LogicalStroke> answer) =>
        MultiLineScoreV1.Score(target, answer, 0.02, 256);
}
