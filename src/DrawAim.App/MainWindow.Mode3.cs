using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DrawAim.App.Controls;
using DrawAim.Core.Color;
using DrawAim.Core.Generation;
using DrawAim.Infrastructure.History;

namespace DrawAim.App;

public partial class MainWindow
{
    private void GenerateMode3Question()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _mode3QuestionActive = false;
        _mode3Submitted = true;
        Mode3SubmitButton.IsEnabled = false;
        Mode3ColorPicker.IsEnabled = false;
        Mode3Canvas.IsInputEnabled = false;
        Mode3Canvas.Clear();
        Mode3TargetSwatch.Background = null;
        ClearMode3Dimensions();

        int difficulty = (int)Math.Round(Mode3Difficulty.Value);
        bool questionPracticeMode = Mode3PracticeMode.IsChecked == true;
        var settings = new ColorGenerationSettings
        {
            MinimumLightness = Mode3IncludeBlack.IsChecked == true ? 0.025 : 0.12,
            MaximumLightness = Mode3IncludeWhite.IsChecked == true ? 0.985 : 0.92,
            MinimumChroma = Mode3IncludeLowChroma.IsChecked == true ? 0.004 : 0.035,
            MaximumChroma = Math.Clamp(0.21 + (difficulty * 0.01), 0.22, 0.31),
            IncludeNearWhite = Mode3IncludeWhite.IsChecked == true,
            IncludeNearBlack = Mode3IncludeBlack.IsChecked == true,
            IncludeLowChroma = Mode3IncludeLowChroma.IsChecked == true,
            Difficulty = difficulty,
            PreviousColor = _mode3ExerciseIndex > 0 ? _mode3Target : null,
            MinimumPreviousDeltaE = difficulty >= 7 ? 8 : 12,
            MaximumAttempts = 320,
        };
        ulong seed = ResolveQuestionSeed(Mode3Seed, Mode3LockSeed, 20260803);
        string questionFingerprint = BuildFingerprint(TrainingModeKind.ColorMatch);
        var key = new GenerationKey(
            TargetColorGenerator.Version,
            ExerciseMode.ColorMatch,
            seed,
            _mode3ExerciseIndex,
            questionFingerprint,
            1,
            1);
        GenerationResult<ColorTarget> generated = _colorGenerator.Generate(key, settings);
        if (!generated.IsSuccess)
        {
            Mode3Similarity.Text = "—";
            Mode3SimilarityHint.Text = $"无法生成目标色：{generated.Error?.Message ?? "设置无解"}";
            return;
        }

        _mode3Target = generated.Value.Srgb;
        _mode3QuestionFingerprint = questionFingerprint;
        _mode3QuestionSeed = seed;
        _mode3QuestionPracticeMode = questionPracticeMode;
        _mode3QuestionActive = true;
        _mode3Submitted = false;
        Mode3SubmitButton.IsEnabled = true;
        Mode3ColorPicker.IsEnabled = true;
        Mode3Canvas.IsInputEnabled = true;
        Mode3ColorPicker_SelectedColorChanged(
            Mode3ColorPicker,
            new HsvColorChangedEventArgs(
                Mode3ColorPicker.SelectedHsv,
                Mode3ColorPicker.SelectedColor));
        Mode3TargetSwatch.Background = ToBrush(_mode3Target);
        UpdateMode3PracticeScore();
        Mode3SimilarityHint.Text = generated.UsedFallback
            ? "使用了确定性色域内降级颜色"
            : _mode3QuestionPracticeMode
                ? "练习模式：选择时实时更新"
                : "测试模式：提交前隐藏接近度";
    }

    private void Mode3ColorPicker_SelectedColorChanged(object? sender, HsvColorChangedEventArgs e)
    {
        if (!_isUiInitialized || !_mode3QuestionActive || _mode3Submitted)
        {
            return;
        }

        _mode3Selected = e.Color;
        Mode3PlayerSwatch.Background = ToBrush(e.Color);
        Mode3Canvas.StrokeBrush = ToBrush(e.Color);
        Mode3ColorCode.Text =
            $"{ToHex(e.Color)} · HSV {e.Hsv.HueDegrees:F0}°, {e.Hsv.Saturation * 100:F0}%, {e.Hsv.Value * 100:F0}%";
        ClearMode3Dimensions();
        UpdateMode3PracticeScore();
    }

    private void UpdateMode3PracticeScore()
    {
        if (Mode3Similarity is null)
        {
            return;
        }

        if (_mode3QuestionPracticeMode)
        {
            ColorScoreResult result = ColorScoreV1.Score(_mode3Target, _mode3Selected);
            Mode3Similarity.Text = $"{result.Similarity:F1}%";
            Mode3SimilarityHint.Text = "练习模式：当前选择的实时接近度";
        }
        else if (!_mode3Submitted)
        {
            Mode3Similarity.Text = "隐藏";
            Mode3SimilarityHint.Text = "测试模式：提交后显示结果";
        }
    }

    private void Mode3Submit_Click(object sender, RoutedEventArgs e)
    {
        if (!_mode3QuestionActive || _mode3Submitted)
        {
            return;
        }

        ColorScoreResult result = ColorScoreV1.Score(_mode3Target, _mode3Selected);
        _mode3Submitted = true;
        Mode3SubmitButton.IsEnabled = false;
        Mode3ColorPicker.IsEnabled = false;
        Mode3Canvas.CancelActiveStroke();
        Mode3Canvas.IsInputEnabled = false;
        ShowMode3Result(result);
        var components = new Dictionary<string, double>
        {
            ["DeltaEOK"] = result.DeltaEOK,
            ["DeltaLightness"] = result.DeltaLightness,
            ["DeltaChroma"] = result.DeltaChroma,
            ["DeltaHsvValue"] = result.DeltaHsvValue,
        };
        if (result.DeltaHsvSaturation is double saturationDelta)
        {
            components["DeltaHsvSaturation"] = saturationDelta;
        }

        if (result.DeltaHueDegrees is double hueDelta)
        {
            components["DeltaHueDegrees"] = hueDelta;
        }

        TrackHistoryWrite(RecordCompletedAsync(
            TrainingModeKind.ColorMatch,
            result.Similarity,
            0,
            _mode3QuestionSeed,
            _mode3ExerciseIndex,
            TargetColorGenerator.Version,
            ColorScoreV1.Version,
            components,
            _mode3QuestionFingerprint,
            "NotApplicable"));
    }

    private void ShowMode3Result(ColorScoreResult result)
    {
        Mode3Similarity.Text = $"{result.Similarity:F1}%";
        Mode3SimilarityHint.Text = result.Similarity switch
        {
            >= 95 => "颜色判断非常接近",
            >= 80 => "已经接近，检查最明显的维度",
            >= 55 => "观察明度和彩度谁偏得更多",
            _ => "差异较大，先从明度开始校正",
        };
        Mode3DeltaE.Text = $"{result.DeltaEOK:F2}";
        Mode3DeltaL.Text = DescribeSigned(result.DeltaLightness, "偏亮", "偏暗", string.Empty);
        Mode3DeltaC.Text = DescribeSigned(result.DeltaChroma, "偏鲜艳", "偏灰", string.Empty);
        Mode3DeltaS.Text = result.DeltaHsvSaturation.HasValue
            ? DescribeSigned(result.DeltaHsvSaturation.Value, "偏高", "偏低", " 个百分点")
            : "不可判定";
        Mode3DeltaV.Text = DescribeSigned(result.DeltaHsvValue, "偏高", "偏低", " 个百分点");
        Mode3DeltaH.Text = result.DeltaHueDegrees.HasValue
            ? $"{result.DeltaHueDegrees.Value:+0.0;-0.0;0.0}°"
            : "色相差异不可判定";
    }

    private static string DescribeSigned(
        double value,
        string positive,
        string negative,
        string suffix) => Math.Abs(value) < 0.005
        ? $"相同 0.00{suffix}"
        : value > 0
            ? $"{positive} +{value:F2}{suffix}"
            : $"{negative} {value:F2}{suffix}";

    private void ClearMode3Dimensions()
    {
        if (Mode3DeltaE is null)
        {
            return;
        }

        Mode3DeltaE.Text = "—";
        Mode3DeltaL.Text = "—";
        Mode3DeltaC.Text = "—";
        Mode3DeltaS.Text = "—";
        Mode3DeltaV.Text = "—";
        Mode3DeltaH.Text = "—";
    }

    private void Mode3NewQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_mode3QuestionActive && !_mode3Submitted)
        {
            TrackHistoryWrite(RecordSkippedAsync(
                TrainingModeKind.ColorMatch,
                _mode3QuestionSeed,
                _mode3ExerciseIndex,
                TargetColorGenerator.Version,
                _mode3QuestionFingerprint));
        }

        _mode3ExerciseIndex++;
        GenerateMode3Question();
    }

    private void Mode3Undo_Click(object sender, RoutedEventArgs e) => Mode3Canvas.Undo();

    private void Mode3Redo_Click(object sender, RoutedEventArgs e) => Mode3Canvas.Redo();

    private void Mode3Clear_Click(object sender, RoutedEventArgs e) => Mode3Canvas.Clear();

    private void Mode3BrushSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        Mode3BrushText.Text = $"{e.NewValue:F0}";
        Mode3Canvas.BrushSize = e.NewValue;
    }

    private void Mode3Stability_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        int level = (int)Math.Round(e.NewValue);
        Mode3StabilityText.Text = StabilityLabel(level);
        Mode3Canvas.StabilizerLevel = level;
    }

    private void Mode3PracticeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitialized && !_mode3Submitted)
        {
            Mode3SimilarityHint.Text = Mode3PracticeMode.IsChecked == _mode3QuestionPracticeMode
                ? _mode3QuestionPracticeMode
                    ? "练习模式：当前选择的实时接近度"
                    : "测试模式：提交后显示结果"
                : "训练方式已更改，将从下一题生效；本题条件保持不变。";
        }
    }

    private void Mode3Settings_Changed(object sender, EventArgs e)
    {
        if (_isUiInitialized)
        {
            Mode3DifficultyText.Text = Math.Round(Mode3Difficulty.Value)
                .ToString(CultureInfo.InvariantCulture);
            if (_mode3QuestionActive && !_mode3Submitted)
            {
                Mode3SimilarityHint.Text = "目标设置已更新，将从下一题生效。";
            }
        }
    }

    private static SolidColorBrush ToBrush(SrgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(
            ToByte(color.R),
            ToByte(color.G),
            ToByte(color.B)));
        brush.Freeze();
        return brush;
    }

    private static string ToHex(SrgbColor color) =>
        $"#{ToByte(color.R):X2}{ToByte(color.G):X2}{ToByte(color.B):X2}";

    private static byte ToByte(double channel) =>
        (byte)Math.Clamp((int)Math.Round(Math.Clamp(channel, 0, 1) * 255), 0, 255);
}
