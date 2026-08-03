using DrawAim.Core.Generation;
using DrawAim.Core.Geometry;
using DrawAim.Core.Randomness;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> GenerationTests()
    {
        yield return new TestCase("Generation/PCG32 黄金向量", PcgGoldenVector);
        yield return new TestCase("Generation/Seed 派生确定性", SeedDerivationIsDeterministic);
        yield return new TestCase("Generation/固定生成契约黄金向量", FixedGenerationContractGoldenVector);
        yield return new TestCase("Generation/相同 Key 产生相同单线", SameKeyProducesSameLine);
        yield return new TestCase("Generation/不同题号通常产生不同单线", DifferentIndexesProduceDifferentLines);
        yield return new TestCase("Generation/批量单线不越界不自交", GeneratedLinesAreValid);
        yield return new TestCase("Generation/方向与位置分布均衡", DirectionAndPositionAreBalanced);
        yield return new TestCase("Generation/连续题目肉眼多样性", AdjacentExercisesAvoidPerceptualDuplicates);
        yield return new TestCase("Generation/线型权重严格生效", LineKindWeightsAreHonored);
        yield return new TestCase("Generation/无效权重明确失败", InvalidWeightsFailExplicitly);
        yield return new TestCase("Generation/极端权重总和溢出明确失败", OverflowingWeightsFailExplicitly);
        yield return new TestCase("Generation/单线确定性降级不越过设置范围", LineFallbackRespectsSettings);
        yield return new TestCase("Generation/无解单线设置有限失败", ImpossibleLineSettingsFail);
        yield return new TestCase("Generation/组合数量与边界有效", MultiLineCountAndBoundsAreValid);
        yield return new TestCase("Generation/禁止相交设置生效", MultiLineIntersectionRuleIsHonored);
        yield return new TestCase("Generation/组合双向近重合受限", MultiLineNearOverlapIsRejectedBidirectionally);
        yield return new TestCase("Generation/零间距仍禁止重复线", ZeroSeparationStillRejectsDuplicateLines);
        yield return new TestCase("Generation/允许相交仍限制交点聚簇", AllowedIntersectionsStillLimitDenseClusters);
        yield return new TestCase("Generation/相同 Key 产生相同组合", SameKeyProducesSameComposition);
        yield return new TestCase("Generation/颜色确定且始终在 sRGB 色域", GeneratedColorsAreDeterministicAndInGamut);
        yield return new TestCase("Generation/无解颜色范围明确失败", ImpossibleColorRangeFails);
    }

    private static void PcgGoldenVector()
    {
        var random = new Pcg32(42, 54);
        uint[] expected = [0xA15C02B7, 0x7B47F409, 0xBA1D3330, 0x83D2F293, 0xBFA4784B];
        foreach (var value in expected)
        {
            AssertEx.Equal(value, random.NextUInt32());
        }
    }

    private static void SeedDerivationIsDeterministic()
    {
        var key = LineKey(123, 7);
        var first = SeedDerivation.Derive(key, 1, 2, 3);
        var second = SeedDerivation.Derive(key, 1, 2, 3);
        AssertEx.Equal(first, second);
        AssertEx.False(first == SeedDerivation.Derive(key, 1, 2, 4), "附加字段没有进入 Seed 派生。" );
    }

    private static void FixedGenerationContractGoldenVector()
    {
        var seed = SeedDerivation.Derive(LineKey(123, 7), 1, 2, 3);
        var line = new TargetLineGenerator().Generate(
            LineKey(0x1234_5678, 19),
            LineGenerationSettings.Default);
        var composition = new MultiLineGenerator().Generate(
            CompositionKey(0xCAFE, 12),
            MultiLineGenerationSettings.Default);
        var color = new TargetColorGenerator().Generate(
            ColorKey(123, 17),
            ColorGenerationSettings.Default);
        AssertEx.True(line.IsSuccess && composition.IsSuccess && color.IsSuccess, "黄金向量生成失败。" );
        var firstCompositionLine = composition.Value.Lines[0];
        AssertEx.Equal(569_457_044_360_427_090UL, seed);
        AssertEx.Equal(CurveKind.CShape, line.Value.Kind);
        AssertEx.Near(221.97032156151167, line.Value.Bezier.P0.X, 1e-10);
        AssertEx.Near(228.13078472450425, line.Value.Bezier.P0.Y, 1e-10);
        AssertEx.Near(604.77266482522361, line.Value.Bezier.P3.X, 1e-10);
        AssertEx.Near(211.64312294147251, line.Value.Bezier.P3.Y, 1e-10);
        AssertEx.False(line.Value.SuggestedForward, "单线方向提示黄金值改变。" );
        AssertEx.Equal(4, composition.Value.Lines.Count);
        AssertEx.Equal(CurveKind.SShape, firstCompositionLine.Kind);
        AssertEx.Near(0.14145502708222657, firstCompositionLine.Bezier.P0.X, 1e-12);
        AssertEx.Near(0.10483708078986977, firstCompositionLine.Bezier.P0.Y, 1e-12);
        AssertEx.Near(0.41009208632462613, firstCompositionLine.Bezier.P3.X, 1e-12);
        AssertEx.Near(0.17573953494925187, firstCompositionLine.Bezier.P3.Y, 1e-12);
        AssertEx.Near(0.71236030464265310, color.Value.Srgb.R, 1e-12);
        AssertEx.Near(0.82012684799218227, color.Value.Srgb.G, 1e-12);
        AssertEx.Near(0.98678833823099532, color.Value.Srgb.B, 1e-12);
    }

    private static void SameKeyProducesSameLine()
    {
        var generator = new TargetLineGenerator();
        var key = LineKey(0x1234_5678, 19);
        var first = generator.Generate(key, LineGenerationSettings.Default);
        var second = generator.Generate(key, LineGenerationSettings.Default);
        AssertEx.True(first.IsSuccess && second.IsSuccess, "默认单线生成失败。" );
        AssertEx.Equal(first.Value.Kind, second.Value.Kind);
        AssertEx.Equal(first.Value.Bezier, second.Value.Bezier);
        AssertEx.Equal(first.Value.SuggestedForward, second.Value.SuggestedForward);
    }

    private static void DifferentIndexesProduceDifferentLines()
    {
        var generator = new TargetLineGenerator();
        var first = generator.Generate(LineKey(7, 10), LineGenerationSettings.Default);
        var second = generator.Generate(LineKey(7, 11), LineGenerationSettings.Default);
        AssertEx.True(first.IsSuccess && second.IsSuccess, "默认单线生成失败。" );
        AssertEx.False(first.Value.Bezier == second.Value.Bezier, "不同题号生成了完全相同的曲线。" );
    }

    private static void GeneratedLinesAreValid()
    {
        var generator = new TargetLineGenerator();
        const double width = 1000;
        const double height = 700;
        var margin = Math.Min(width, height) * LineGenerationSettings.Default.SafeMarginRatio;
        var safe = new Rect2(margin, margin, width - (2 * margin), height - (2 * margin));
        for (var index = 0; index < 300; index++)
        {
            var result = generator.Generate(LineKey(0xA55A, index), LineGenerationSettings.Default);
            AssertEx.True(result.IsSuccess, $"第 {index} 条默认曲线生成失败：{result.Error?.Message}" );
            var target = result.Value;
            AssertEx.True(
                safe.Contains(new Point2(target.Bounds.Left, target.Bounds.Top), 1e-7) &&
                safe.Contains(new Point2(target.Bounds.Right, target.Bounds.Bottom), 1e-7),
                $"第 {index} 条曲线越过安全边界。" );
            AssertEx.False(GeometryMath.HasSelfIntersection(target.Polyline), $"第 {index} 条曲线自交。" );
            AssertEx.InRange(
                target.Length / Math.Min(width, height),
                LineGenerationSettings.Default.MinimumLengthRatio - 0.002,
                LineGenerationSettings.Default.MaximumLengthRatio + 0.002);
            for (var sample = 0; sample <= 64; sample++)
            {
                AssertEx.True(
                    target.Bezier.Derivative(sample / 64.0).Length > 1e-6,
                    $"第 {index} 条曲线出现 cusp。" );
            }
        }
    }

    private static void DirectionAndPositionAreBalanced()
    {
        var generator = new TargetLineGenerator();
        var directionBins = new int[16];
        var quadrantBins = new int[4];
        const int count = 512;
        for (var index = 0; index < count; index++)
        {
            var generated = generator.Generate(LineKey(0xD157, index), LineGenerationSettings.Default);
            AssertEx.True(generated.IsSuccess, $"分布样本 {index} 生成失败。" );
            var chord = generated.Value.Bezier.P3 - generated.Value.Bezier.P0;
            var angle = Math.Atan2(chord.Y, chord.X) * 180 / Math.PI;
            if (angle < 0)
            {
                angle += 360;
            }

            var directionBin = Math.Min(15, (int)(angle / 22.5));
            directionBins[directionBin]++;
            var center = new Point2(
                (generated.Value.Bounds.Left + generated.Value.Bounds.Right) / 2,
                (generated.Value.Bounds.Top + generated.Value.Bounds.Bottom) / 2);
            var quadrant = (center.X >= 500 ? 1 : 0) + (center.Y >= 350 ? 2 : 0);
            quadrantBins[quadrant]++;
        }

        foreach (var bin in directionBins)
        {
            AssertEx.InRange(bin, 20, 44, $"方向桶分布失衡：{string.Join(",", directionBins)}" );
        }

        foreach (var bin in quadrantBins)
        {
            AssertEx.InRange(bin, 95, 161, $"位置象限分布失衡：{string.Join(",", quadrantBins)}" );
        }
    }

    private static void AdjacentExercisesAvoidPerceptualDuplicates()
    {
        var scenarios = new[]
        {
            ("default", LineGenerationSettings.Default, 1000.0, 700.0),
            ("straight", LineGenerationSettings.Default with
            {
                StraightWeight = 1,
                CShapeWeight = 0,
                SShapeWeight = 0,
            }, 1000.0, 700.0),
            ("c-only", LineGenerationSettings.Default with
            {
                StraightWeight = 0,
                CShapeWeight = 1,
                SShapeWeight = 0,
            }, 1000.0, 700.0),
            ("s-only", LineGenerationSettings.Default with
            {
                StraightWeight = 0,
                CShapeWeight = 0,
                SShapeWeight = 1,
            }, 1000.0, 700.0),
            ("fallback-straight", LineGenerationSettings.Default with
            {
                StraightWeight = 1,
                CShapeWeight = 0,
                SShapeWeight = 0,
                MinimumLengthRatio = 0.90,
                MaximumLengthRatio = 0.90,
                SafeMarginRatio = 0.08,
                MaximumAttempts = 1,
            }, 200.0, 100.0),
            ("fallback-c", LineGenerationSettings.Default with
            {
                StraightWeight = 0,
                CShapeWeight = 1,
                SShapeWeight = 0,
                MinimumLengthRatio = 0.82,
                MaximumLengthRatio = 0.82,
                MinimumCurvatureRatio = 0.22,
                MaximumCurvatureRatio = 0.30,
                SafeMarginRatio = 0.08,
                MaximumAttempts = 1,
            }, 200.0, 100.0),
            ("narrow-direction", LineGenerationSettings.Default with
            {
                StraightWeight = 1,
                CShapeWeight = 0,
                SShapeWeight = 0,
                MinimumDirectionDegrees = 15,
                MaximumDirectionDegrees = 35,
            }, 1000.0, 700.0),
            ("narrow-c", LineGenerationSettings.Default with
            {
                StraightWeight = 0,
                CShapeWeight = 1,
                SShapeWeight = 0,
                MinimumLengthRatio = 0.465,
                MaximumLengthRatio = 0.575,
                MinimumCurvatureRatio = 0.145,
                MaximumCurvatureRatio = 0.215,
                MinimumDirectionDegrees = 85,
                MaximumDirectionDegrees = 95,
            }, 780.0, 580.0),
            ("narrow-s", LineGenerationSettings.Default with
            {
                StraightWeight = 0,
                CShapeWeight = 0,
                SShapeWeight = 1,
                MinimumLengthRatio = 0.465,
                MaximumLengthRatio = 0.575,
                MinimumCurvatureRatio = 0.145,
                MaximumCurvatureRatio = 0.215,
                MinimumDirectionDegrees = 85,
                MaximumDirectionDegrees = 95,
            }, 780.0, 580.0),
        };
        ulong[] baseSeeds = [1, 0xD1A3_5517, 0xCAFE_BABE, ulong.MaxValue - 17];
        var summaries = new List<string>();
        foreach (var (name, settings, width, height) in scenarios)
        {
            var comparable = 0;
            var nearDuplicates = 0;
            var exactDuplicates = 0;
            var adjacentFallbacks = 0;
            var examples = new List<string>();
            foreach (var baseSeed in baseSeeds)
            {
                TargetCurve? previous = null;
                var previousFallback = false;
                for (var index = 0; index < 1_024; index++)
                {
                    var result = new TargetLineGenerator().Generate(
                        new GenerationKey(
                            TargetLineGenerator.Version,
                            ExerciseMode.LineTrace,
                            baseSeed,
                            index,
                            name,
                            width,
                            height),
                        settings);
                    if (!result.IsSuccess)
                    {
                        previous = null;
                        previousFallback = false;
                        continue;
                    }

                    AssertEx.InRange(
                        result.Value.Length / Math.Min(width, height),
                        settings.MinimumLengthRatio - 0.002,
                        settings.MaximumLengthRatio + 0.002,
                        $"{name} 连续题测试中的长度越过设置范围。" );

                    if (previous is not null)
                    {
                        comparable++;
                        var isNearDuplicate = ArePerceptuallyNearDuplicates(
                            previous,
                            result.Value,
                            width,
                            height);
                        nearDuplicates += isNearDuplicate ? 1 : 0;
                        if (isNearDuplicate && examples.Count < 8)
                        {
                            examples.Add($"seed={baseSeed},{index - 1}->{index}[{previousFallback}->{result.UsedFallback}]:{DescribeCurve(previous, width, height)} | {DescribeCurve(result.Value, width, height)}");
                        }

                        exactDuplicates += previous.Bezier == result.Value.Bezier ? 1 : 0;
                        adjacentFallbacks += previousFallback && result.UsedFallback ? 1 : 0;
                    }

                    previous = result.Value;
                    previousFallback = result.UsedFallback;
                }
            }

            var summary =
                $"{name}: pairs={comparable}, near={nearDuplicates}, exact={exactDuplicates}, bothFallback={adjacentFallbacks}, examples=[{string.Join(" / ", examples)}]";
            summaries.Add(summary);
            AssertEx.True(
                nearDuplicates == 0 && exactDuplicates == 0,
                $"相邻题出现肉眼近似重复。{summary}" );
            AssertEx.InRange(
                comparable,
                4_000,
                4_092,
                $"{name} 场景有效相邻题不足：{comparable}。" );
        }

        AssertEx.Equal(scenarios.Length, summaries.Count);
    }

    private static bool ArePerceptuallyNearDuplicates(
        TargetCurve first,
        TargetCurve second,
        double canvasWidth,
        double canvasHeight)
    {
        if (first.Kind != second.Kind)
        {
            return false;
        }

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var firstCenter = new Point2(
            (first.Bounds.Left + first.Bounds.Right) / 2,
            (first.Bounds.Top + first.Bounds.Bottom) / 2);
        var secondCenter = new Point2(
            (second.Bounds.Left + second.Bounds.Right) / 2,
            (second.Bounds.Top + second.Bounds.Bottom) / 2);
        var centerDistance = Point2.Distance(firstCenter, secondCenter) / shortSide;
        var relativeLengthDifference = Math.Abs(first.Length - second.Length) /
                                       Math.Max(first.Length, second.Length);
        var firstAngle = Math.Atan2(
            first.Bezier.P3.Y - first.Bezier.P0.Y,
            first.Bezier.P3.X - first.Bezier.P0.X);
        var secondAngle = Math.Atan2(
            second.Bezier.P3.Y - second.Bezier.P0.Y,
            second.Bezier.P3.X - second.Bezier.P0.X);
        var angleDifference = Math.Abs(firstAngle - secondAngle) * 180 / Math.PI % 180;
        angleDifference = Math.Min(angleDifference, 180 - angleDifference);
        var firstBend = MaximumChordDeviation(first) / Math.Max(first.Length, 1e-9);
        var secondBend = MaximumChordDeviation(second) / Math.Max(second.Length, 1e-9);
        return centerDistance <= 0.10 &&
               relativeLengthDifference <= 0.10 &&
               angleDifference <= 10 &&
               Math.Abs(firstBend - secondBend) <= 0.035;
    }

    private static double MaximumChordDeviation(TargetCurve curve) =>
        curve.Polyline.Max(point => GeometryMath.DistanceToSegment(
            point,
            curve.Bezier.P0,
            curve.Bezier.P3));

    private static string DescribeCurve(TargetCurve curve, double width, double height)
    {
        var center = new Point2(
            (curve.Bounds.Left + curve.Bounds.Right) / 2 / width,
            (curve.Bounds.Top + curve.Bounds.Bottom) / 2 / height);
        var angle = Math.Atan2(
            curve.Bezier.P3.Y - curve.Bezier.P0.Y,
            curve.Bezier.P3.X - curve.Bezier.P0.X) * 180 / Math.PI;
        return $"{curve.Kind},c=({center.X:F3},{center.Y:F3}),a={angle:F1},l={curve.Length:F1},b={MaximumChordDeviation(curve) / curve.Length:F3}";
    }

    private static void LineKindWeightsAreHonored()
    {
        var generator = new TargetLineGenerator();
        var cOnly = LineGenerationSettings.Default with
        {
            StraightWeight = 0,
            CShapeWeight = 1,
            SShapeWeight = 0,
        };
        var sOnly = cOnly with { CShapeWeight = 0, SShapeWeight = 1 };
        for (var index = 0; index < 20; index++)
        {
            AssertEx.Equal(CurveKind.CShape, generator.Generate(LineKey(1, index), cOnly).Value.Kind);
            AssertEx.Equal(CurveKind.SShape, generator.Generate(LineKey(2, index), sOnly).Value.Kind);
        }

        var weighted = LineGenerationSettings.Default with
        {
            StraightWeight = 1,
            CShapeWeight = 2,
            SShapeWeight = 3,
        };
        var counts = new int[3];
        for (var index = 0; index < 1_800; index++)
        {
            var result = generator.Generate(LineKey(0x6123, index), weighted);
            AssertEx.True(result.IsSuccess, $"权重分布样本 {index} 生成失败。" );
            counts[(int)result.Value.Kind]++;
        }

        AssertEx.InRange(counts[(int)CurveKind.Straight], 240, 360,
            $"1:2:3 权重下直线数量异常：{string.Join(',', counts)}" );
        AssertEx.InRange(counts[(int)CurveKind.CShape], 520, 680,
            $"1:2:3 权重下 C 线数量异常：{string.Join(',', counts)}" );
        AssertEx.InRange(counts[(int)CurveKind.SShape], 810, 990,
            $"1:2:3 权重下 S 线数量异常：{string.Join(',', counts)}" );
    }

    private static void InvalidWeightsFailExplicitly()
    {
        var result = new TargetLineGenerator().Generate(
            LineKey(1, 0),
            LineGenerationSettings.Default with
            {
                StraightWeight = 0,
                CShapeWeight = 0,
                SShapeWeight = 0,
            });
        AssertEx.False(result.IsSuccess, "全零权重没有被拒绝。" );
        AssertEx.Equal("InvalidWeights", result.Error?.Code ?? string.Empty);
    }

    private static void OverflowingWeightsFailExplicitly()
    {
        var lineSettings = LineGenerationSettings.Default with
        {
            StraightWeight = 1e308,
            CShapeWeight = 1e308,
            SShapeWeight = 1e308,
        };
        var line = new TargetLineGenerator().Generate(LineKey(1, 0), lineSettings);
        AssertEx.False(line.IsSuccess, "溢出为 Infinity 的单线权重总和没有被拒绝。" );
        AssertEx.Equal("InvalidWeights", line.Error?.Code ?? string.Empty);

        var compositionSettings = MultiLineGenerationSettings.Default with
        {
            StraightWeight = 1e308,
            CShapeWeight = 1e308,
            SShapeWeight = 1e308,
        };
        var composition = new MultiLineGenerator().Generate(
            CompositionKey(1, 0),
            compositionSettings);
        AssertEx.False(composition.IsSuccess, "溢出为 Infinity 的组合权重总和没有被拒绝。" );
        AssertEx.Equal("InvalidWeights", composition.Error?.Code ?? string.Empty);
    }

    private static void LineFallbackRespectsSettings()
    {
        var generator = new TargetLineGenerator();
        var settings = LineGenerationSettings.Default with
        {
            StraightWeight = 1,
            CShapeWeight = 0,
            SShapeWeight = 0,
            MinimumLengthRatio = 0.90,
            MaximumLengthRatio = 0.90,
            SafeMarginRatio = 0.08,
            MaximumAttempts = 1,
        };
        GenerationResult<TargetCurve>? fallback = null;
        GenerationKey? fallbackKey = null;
        for (var index = 0; index < 64; index++)
        {
            var key = new GenerationKey(
                TargetLineGenerator.Version,
                ExerciseMode.LineTrace,
                345,
                index,
                "fallback",
                200,
                100);
            var candidate = generator.Generate(key, settings);
            if (candidate.IsSuccess && candidate.UsedFallback)
            {
                fallback = candidate;
                fallbackKey = key;
                break;
            }
        }

        AssertEx.True(fallback is not null, "未找到可验证的确定性降级样本。" );
        AssertEx.Near(90, fallback!.Value.Length, 0.25, "降级题目越过了长度设置。" );
        var repeated = generator.Generate(fallbackKey!.Value, settings);
        AssertEx.True(repeated.IsSuccess && repeated.UsedFallback, "重复调用没有进入同一降级路径。" );
        AssertEx.Equal(fallback.Value.Bezier, repeated.Value.Bezier);
    }

    private static void ImpossibleLineSettingsFail()
    {
        var result = new TargetLineGenerator().Generate(
            new GenerationKey(
                TargetLineGenerator.Version,
                ExerciseMode.LineTrace,
                1,
                0,
                "impossible",
                100,
                100),
            LineGenerationSettings.Default with
            {
                MinimumLengthRatio = 0.90,
                MaximumLengthRatio = 0.90,
                SafeMarginRatio = 0.44,
                MaximumAttempts = 2,
            });
        AssertEx.False(result.IsSuccess, "明显无解的单线设置被静默修正。" );
        AssertEx.Equal("GenerationFailed", result.Error?.Code ?? string.Empty);
    }

    private static void MultiLineCountAndBoundsAreValid()
    {
        var generator = new MultiLineGenerator();
        var settings = MultiLineGenerationSettings.Default with
        {
            MinimumLineCount = 10,
            MaximumLineCount = 10,
            MinimumLengthRatio = 0.12,
            MaximumLengthRatio = 0.30,
        };
        var safe = new Rect2(
            settings.SafeMarginRatio,
            settings.SafeMarginRatio,
            1 - (2 * settings.SafeMarginRatio),
            1 - (2 * settings.SafeMarginRatio));
        for (var exercise = 0; exercise < 20; exercise++)
        {
            var result = generator.Generate(CompositionKey(88, exercise), settings);
            AssertEx.True(result.IsSuccess, $"10 线组合生成失败：{result.Error?.Message}" );
            AssertEx.Equal(10, result.Value.Lines.Count);
            foreach (var line in result.Value.Lines)
            {
                AssertEx.True(
                    safe.Contains(new Point2(line.Bounds.Left, line.Bounds.Top), 1e-7) &&
                    safe.Contains(new Point2(line.Bounds.Right, line.Bounds.Bottom), 1e-7),
                    "组合曲线越界。" );
                AssertEx.False(GeometryMath.HasSelfIntersection(line.Polyline), "组合中的单线自交。" );
            }
        }
    }

    private static void MultiLineIntersectionRuleIsHonored()
    {
        var generator = new MultiLineGenerator();
        var settings = MultiLineGenerationSettings.Default with
        {
            MinimumLineCount = 7,
            MaximumLineCount = 7,
            AllowIntersections = false,
            MinimumLengthRatio = 0.12,
            MaximumLengthRatio = 0.28,
        };
        for (var exercise = 0; exercise < 20; exercise++)
        {
            var result = generator.Generate(CompositionKey(99, exercise), settings);
            AssertEx.True(result.IsSuccess, $"禁止相交组合生成失败：{result.Error?.Message}" );
            for (var first = 0; first < result.Value.Lines.Count; first++)
            {
                for (var second = first + 1; second < result.Value.Lines.Count; second++)
                {
                    AssertEx.False(
                        GeometryMath.PolylinesIntersect(
                            result.Value.Lines[first].Polyline,
                            result.Value.Lines[second].Polyline),
                        $"组合中的第 {first}、{second} 根线相交。" );
                }
            }
        }
    }

    private static void MultiLineNearOverlapIsRejectedBidirectionally()
    {
        var generator = new MultiLineGenerator();
        var settings = MultiLineGenerationSettings.Default with
        {
            MinimumLineCount = 8,
            MaximumLineCount = 8,
            AllowIntersections = true,
            MinimumLengthRatio = 0.12,
            MaximumLengthRatio = 0.32,
            MinimumSeparationRatio = 0.022,
        };
        for (var exercise = 0; exercise < 20; exercise++)
        {
            var result = generator.Generate(CompositionKey(177, exercise), settings);
            AssertEx.True(result.IsSuccess, $"允许交叉组合生成失败：{result.Error?.Message}" );
            for (var first = 0; first < result.Value.Lines.Count; first++)
            {
                for (var second = first + 1; second < result.Value.Lines.Count; second++)
                {
                    var firstSamples = GeometryMath.ResampleByArcLength(
                        result.Value.Lines[first].Polyline,
                        settings.MinimumSeparationRatio / 2);
                    var secondSamples = GeometryMath.ResampleByArcLength(
                        result.Value.Lines[second].Polyline,
                        settings.MinimumSeparationRatio / 2);
                    var firstClose = firstSamples.Count(point =>
                        GeometryMath.DistanceToPolyline(point, result.Value.Lines[second].Polyline) <
                        settings.MinimumSeparationRatio) / (double)firstSamples.Count;
                    var secondClose = secondSamples.Count(point =>
                        GeometryMath.DistanceToPolyline(point, result.Value.Lines[first].Polyline) <
                        settings.MinimumSeparationRatio) / (double)secondSamples.Count;
                    AssertEx.InRange(firstClose, 0, 0.30 + 1e-12);
                    AssertEx.InRange(secondClose, 0, 0.30 + 1e-12);
                }
            }
        }
    }

    private static void ZeroSeparationStillRejectsDuplicateLines()
    {
        var existing = StraightTarget(new Point2(0.10, 0.50), new Point2(0.90, 0.50));
        var duplicate = StraightTarget(new Point2(0.10, 0.501), new Point2(0.90, 0.501));
        var settings = MultiLineGenerationSettings.Default with
        {
            AllowIntersections = true,
            MinimumSeparationRatio = 0,
        };

        AssertEx.False(
            MultiLineGenerator.IsCompatible(duplicate, [existing], settings),
            "MinimumSeparationRatio=0 关闭了重复/近重合保护。" );
    }

    private static void AllowedIntersectionsStillLimitDenseClusters()
    {
        var horizontal = StraightTarget(new Point2(0.10, 0.50), new Point2(0.90, 0.50));
        var rising = StraightTarget(new Point2(0.15, 0.15), new Point2(0.85, 0.85));
        var falling = StraightTarget(new Point2(0.15, 0.85), new Point2(0.85, 0.15));
        var settings = MultiLineGenerationSettings.Default with
        {
            AllowIntersections = true,
            MinimumSeparationRatio = 0.02,
        };

        AssertEx.True(
            MultiLineGenerator.IsCompatible(rising, [horizontal], settings),
            "普通的两线交叉被错误禁止。" );
        AssertEx.False(
            MultiLineGenerator.IsCompatible(falling, [horizontal, rising], settings),
            "三组交点聚集在同一位置仍被接受。" );
    }

    private static void SameKeyProducesSameComposition()
    {
        var generator = new MultiLineGenerator();
        var key = CompositionKey(0xCAFE, 12);
        var first = generator.Generate(key, MultiLineGenerationSettings.Default);
        var second = generator.Generate(key, MultiLineGenerationSettings.Default);
        AssertEx.True(first.IsSuccess && second.IsSuccess, "默认组合生成失败。" );
        AssertEx.Equal(first.Value.Lines.Count, second.Value.Lines.Count);
        for (var index = 0; index < first.Value.Lines.Count; index++)
        {
            AssertEx.Equal(first.Value.Lines[index].Bezier, second.Value.Lines[index].Bezier);
        }
    }

    private static void GeneratedColorsAreDeterministicAndInGamut()
    {
        var generator = new TargetColorGenerator();
        for (var index = 0; index < 300; index++)
        {
            var key = ColorKey(123, index);
            var first = generator.Generate(key, ColorGenerationSettings.Default);
            var second = generator.Generate(key, ColorGenerationSettings.Default);
            AssertEx.True(first.IsSuccess && second.IsSuccess, "默认颜色生成失败。" );
            AssertEx.Equal(first.Value.Srgb, second.Value.Srgb);
            AssertEx.True(first.Value.Srgb.IsInGamut(), $"第 {index} 个颜色超出 sRGB。" );
            AssertEx.True(first.Value.Oklab.IsFinite && first.Value.Oklch.IsFinite, "颜色空间值不是有限数。" );
        }
    }

    private static void ImpossibleColorRangeFails()
    {
        var result = new TargetColorGenerator().Generate(
            ColorKey(77, 0),
            ColorGenerationSettings.Default with
            {
                MinimumLightness = 0.5,
                MaximumLightness = 0.5,
                MinimumChroma = 0.5,
                MaximumChroma = 0.5,
                MaximumAttempts = 1,
            });
        AssertEx.False(result.IsSuccess, "超出 sRGB 的固定 OKLCh 范围被静默改成其他颜色。" );
        AssertEx.Equal("ColorGenerationFailed", result.Error?.Code ?? string.Empty);
    }

    private static GenerationKey LineKey(ulong seed, int index) =>
        new(TargetLineGenerator.Version, ExerciseMode.LineTrace, seed, index, "default", 1000, 700);

    private static GenerationKey CompositionKey(ulong seed, int index) =>
        new(MultiLineGenerator.Version, ExerciseMode.CompositionCopy, seed, index, "default", 1, 1);

    private static GenerationKey ColorKey(ulong seed, int index) =>
        new(TargetColorGenerator.Version, ExerciseMode.ColorMatch, seed, index, "default", 1, 1);
}
