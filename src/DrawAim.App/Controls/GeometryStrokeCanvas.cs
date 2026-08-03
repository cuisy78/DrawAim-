using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DrawAim.Core.Geometry;
using DrawAim.Core.Input;

namespace DrawAim.App.Controls;

public sealed class StrokeCompletedEventArgs : EventArgs
{
    public StrokeCompletedEventArgs(LogicalStroke stroke, int strokeIndex)
    {
        Stroke = stroke;
        StrokeIndex = strokeIndex;
    }

    public LogicalStroke Stroke { get; }

    public int StrokeIndex { get; }
}

public sealed class StrokeUpdatedEventArgs : EventArgs
{
    public StrokeUpdatedEventArgs(ReadOnlyCollection<LogicalStroke> snapshot, long answerVersion)
    {
        Snapshot = snapshot;
        AnswerVersion = answerVersion;
    }

    public ReadOnlyCollection<LogicalStroke> Snapshot { get; }

    public long AnswerVersion { get; }
}

/// <summary>
/// Low-allocation WPF drawing surface shared by all DrawAim training modes.
/// The geometry displayed by this control is the same stabilized geometry
/// returned from <see cref="GetStrokeSnapshot"/>.
/// </summary>
public sealed class GeometryStrokeCanvas : FrameworkElement
{
    private const int SamplesPerVisualChunk = 48;
    private const double MinimumSampleIntervalSeconds = 1.0 / 4000.0;
    private const double AssumedStylusIntervalSeconds = 1.0 / 240.0;
    private const double StrokeUpdatedIntervalSeconds = 1.0 / 15.0;
    private const double PromotedMouseSuppressionSeconds = 0.08;
    private const int ActiveSampleSoftLimit = 16_384;
    private const int ActiveSampleCompressedTarget = 8_192;
    private const double InitialCompressionToleranceDip = 0.25;
    private const double MaximumAcceptedCoordinateMagnitude = 10_000_000;

    private static readonly Brush DefaultBackground = CreateFrozenBrush(0xFF1B2029);
    private static readonly Brush DefaultStrokeBrush = CreateFrozenBrush(0xFFF3F6FA);
    private static readonly Brush DefaultTargetBrush = CreateFrozenBrush(0xFF778397);
    private static readonly Brush StartHintBrush = CreateFrozenBrush(0xFF5EE0B5);
    private static readonly Pen StartHintPen = CreateFrozenPen(StartHintBrush, 2);

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background),
        typeof(Brush),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(DefaultBackground, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush),
        typeof(Brush),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(DefaultStrokeBrush, OnStrokeBrushChanged));

    public static readonly DependencyProperty RecolorCommittedStrokesOnBrushChangeProperty =
        DependencyProperty.Register(
            nameof(RecolorCommittedStrokesOnBrushChange),
            typeof(bool),
            typeof(GeometryStrokeCanvas),
            new FrameworkPropertyMetadata(false, OnRecolorCommittedStrokesChanged));

    public static readonly DependencyProperty BrushSizeProperty = DependencyProperty.Register(
        nameof(BrushSize),
        typeof(double),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(4.0),
        IsPositiveFiniteDouble);

    public static readonly DependencyProperty UsePressureProperty = DependencyProperty.Register(
        nameof(UsePressure),
        typeof(bool),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty AllowMultipleStrokesProperty = DependencyProperty.Register(
        nameof(AllowMultipleStrokes),
        typeof(bool),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty StabilizerLevelProperty = DependencyProperty.Register(
        nameof(StabilizerLevel),
        typeof(int),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(0),
        static value => value is int level && level is >= 0 and <= 100);

    public static readonly DependencyProperty TargetCurvesProperty = DependencyProperty.Register(
        nameof(TargetCurves),
        typeof(IReadOnlyList<TargetCurve>),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetBrushProperty = DependencyProperty.Register(
        nameof(TargetBrush),
        typeof(Brush),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(DefaultTargetBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetThicknessProperty = DependencyProperty.Register(
        nameof(TargetThickness),
        typeof(double),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender),
        IsPositiveFiniteDouble);

    public static readonly DependencyProperty ShowStartHintProperty = DependencyProperty.Register(
        nameof(ShowStartHint),
        typeof(bool),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsInputEnabledProperty = DependencyProperty.Register(
        nameof(IsInputEnabled),
        typeof(bool),
        typeof(GeometryStrokeCanvas),
        new FrameworkPropertyMetadata(true, OnIsInputEnabledChanged));

    private readonly VisualCollection _strokeVisuals;
    private readonly List<StrokeVisualEntry> _strokes = [];
    private readonly Stack<StrokeVisualEntry> _redo = [];
    private readonly List<StrokeSample> _activeSamples = [];
    private readonly List<StrokeSample> _activeChunkSamples = [];
    private readonly List<DrawingVisual> _activeVisuals = [];

    private StrokeStabilizerV1? _activeStabilizer;
    private ActiveInputDevice _activeInput;
    private bool _strokeLifecycleOpen;
    private Brush _activeBrush = DefaultStrokeBrush;
    private double _activeBrushSize;
    private bool _activeUsesPressure;
    private int _activeStabilizerLevel;
    private double _lastRawTimestamp = double.NegativeInfinity;
    private double _lastStrokeUpdatedNotification = double.NegativeInfinity;
    private double _lastNonMouseInputTimestamp = double.NegativeInfinity;
    private Window? _hostWindow;

    public GeometryStrokeCanvas()
    {
        _strokeVisuals = new VisualCollection(this);
        ClipToBounds = true;
        Focusable = true;
        IsHitTestVisible = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += OnEnabledChanged;
    }

    public event EventHandler? StrokeStarted;

    public event EventHandler<StrokeUpdatedEventArgs>? StrokeUpdated;

    public event EventHandler<StrokeCompletedEventArgs>? StrokeCompleted;

    public event EventHandler? StrokeCancelled;

    public event EventHandler? StrokesChanged;

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public bool RecolorCommittedStrokesOnBrushChange
    {
        get => (bool)GetValue(RecolorCommittedStrokesOnBrushChangeProperty);
        set => SetValue(RecolorCommittedStrokesOnBrushChangeProperty, value);
    }

    public double BrushSize
    {
        get => (double)GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    public bool UsePressure
    {
        get => (bool)GetValue(UsePressureProperty);
        set => SetValue(UsePressureProperty, value);
    }

    public bool AllowMultipleStrokes
    {
        get => (bool)GetValue(AllowMultipleStrokesProperty);
        set => SetValue(AllowMultipleStrokesProperty, value);
    }

    public int StabilizerLevel
    {
        get => (int)GetValue(StabilizerLevelProperty);
        set => SetValue(StabilizerLevelProperty, value);
    }

    public IReadOnlyList<TargetCurve> TargetCurves
    {
        get => (IReadOnlyList<TargetCurve>?)GetValue(TargetCurvesProperty) ?? Array.Empty<TargetCurve>();
        set => SetValue(TargetCurvesProperty, value ?? Array.Empty<TargetCurve>());
    }

    public Brush TargetBrush
    {
        get => (Brush)GetValue(TargetBrushProperty);
        set => SetValue(TargetBrushProperty, value);
    }

    public double TargetThickness
    {
        get => (double)GetValue(TargetThicknessProperty);
        set => SetValue(TargetThicknessProperty, value);
    }

    public bool ShowStartHint
    {
        get => (bool)GetValue(ShowStartHintProperty);
        set => SetValue(ShowStartHintProperty, value);
    }

    public bool IsInputEnabled
    {
        get => (bool)GetValue(IsInputEnabledProperty);
        set => SetValue(IsInputEnabledProperty, value);
    }

    public int StrokeCount => _strokes.Count;

    public bool HasActiveStroke => _strokeLifecycleOpen;

    public bool IsDrawing => HasActiveStroke;

    public bool CanUndo => _strokes.Count > 0 && !HasActiveStroke;

    public bool CanRedo => _redo.Count > 0 && !HasActiveStroke;

    public long AnswerVersion { get; private set; }

    protected override int VisualChildrenCount => _strokeVisuals.Count;

    public ReadOnlyCollection<LogicalStroke> GetStrokeSnapshot()
    {
        var snapshot = _strokes.Select(static entry => entry.Stroke).ToArray();
        return Array.AsReadOnly(snapshot);
    }

    public ReadOnlyCollection<LogicalStroke> GetStrokeSnapshotIncludingActive()
    {
        var snapshot = new List<LogicalStroke>(_strokes.Count + 1);
        snapshot.AddRange(_strokes.Select(static entry => entry.Stroke));
        if (_activeSamples.Count > 0)
        {
            snapshot.Add(new LogicalStroke(
                _activeSamples.ToArray(),
                _activeStabilizerLevel,
                StrokeStabilizerV1.Version));
        }

        return Array.AsReadOnly(snapshot.ToArray());
    }

    /// <summary>
    /// Rebuilds only the presentation visuals for submitted and redoable strokes.
    /// Logical geometry, pressure, brush size, active input and undo/redo order are preserved.
    /// </summary>
    public void RecolorCommittedStrokes()
    {
        VerifyAccess();
        if (_strokes.Count == 0 && _redo.Count == 0)
        {
            return;
        }

        var brush = CloneAndFreeze(StrokeBrush);
        var replacements = new List<StrokeVisualReplacement>(_strokes.Count + _redo.Count);
        foreach (var entry in _strokes)
        {
            replacements.Add(CreateReplacement(entry, brush));
        }

        foreach (var entry in _redo)
        {
            replacements.Add(CreateReplacement(entry, brush));
        }

        // Stage every replacement before mutating the visual tree. A failure cannot
        // leave half the answer redrawn or disturb an active captured stroke.
        _strokeVisuals.Clear();
        foreach (var replacement in replacements)
        {
            replacement.Entry.Brush = brush;
            replacement.Entry.Visuals = replacement.Visuals;
        }

        foreach (var entry in _strokes)
        {
            foreach (var visual in entry.Visuals)
            {
                _strokeVisuals.Add(visual);
            }
        }

        // Active visuals keep the color and brush settings locked at pen-down.
        foreach (var visual in _activeVisuals)
        {
            _strokeVisuals.Add(visual);
        }

        InvalidateVisual();
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        var entry = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        RemoveVisuals(entry.Visuals);
        _redo.Push(entry);
        NotifyAnswerChanged();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        var entry = _redo.Pop();
        _strokes.Add(entry);
        foreach (var visual in entry.Visuals)
        {
            _strokeVisuals.Add(visual);
        }

        NotifyAnswerChanged();
        return true;
    }

    public void Clear()
    {
        CancelActiveStroke(false);
        _strokeVisuals.Clear();
        _strokes.Clear();
        _redo.Clear();
        NotifyAnswerChanged();
    }

    public void CancelActiveStroke() => CancelActiveStroke(true);

    protected override Visual GetVisualChild(int index) => _strokeVisuals[index];

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Background, null, bounds);

        var targetPen = new Pen(TargetBrush, TargetThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        foreach (var curve in TargetCurves)
        {
            DrawTargetCurve(drawingContext, curve, targetPen);
            if (ShowStartHint)
            {
                DrawStartHint(drawingContext, curve);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsPromotedOrRecentNonMouseEvent(e) || !CanStartStroke())
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!IsInsideCanvas(point))
        {
            return;
        }

        Focus();
        e.Handled = true;
        if (!CaptureMouse() || !IsMouseCaptured)
        {
            RaiseCaptureFailureCancellation();
            return;
        }

        if (!BeginStroke(ActiveInputDevice.Mouse))
        {
            ReleaseMouseCapture();
            RaiseCaptureFailureCancellation();
            return;
        }

        AppendRawSample(point, 1, GetTimestampSeconds());
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_activeInput != ActiveInputDevice.Mouse || IsPromotedOrRecentNonMouseEvent(e))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CompleteActiveStroke();
            e.Handled = true;
            return;
        }

        AppendRawSample(e.GetPosition(this), 1, GetTimestampSeconds());
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_activeInput != ActiveInputDevice.Mouse || IsPromotedOrRecentNonMouseEvent(e))
        {
            return;
        }

        AppendRawSample(e.GetPosition(this), 1, GetTimestampSeconds());
        CompleteActiveStroke();
        e.Handled = true;
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        base.OnStylusDown(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        if (e.StylusDevice.Inverted ||
            e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus ||
            !CanStartStroke())
        {
            e.Handled = true;
            return;
        }

        var point = e.GetPosition(this);
        if (!IsInsideCanvas(point))
        {
            e.Handled = true;
            return;
        }

        Focus();
        e.Handled = true;
        if (!CaptureStylus() || !IsStylusCaptured)
        {
            RaiseCaptureFailureCancellation();
            return;
        }

        if (!BeginStroke(ActiveInputDevice.Stylus))
        {
            ReleaseStylusCapture();
            RaiseCaptureFailureCancellation();
            return;
        }

        AppendStylusPoints(e.GetStylusPoints(this));
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        base.OnStylusMove(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
        if (_activeInput != ActiveInputDevice.Stylus)
        {
            return;
        }

        AppendStylusPoints(e.GetStylusPoints(this));
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        base.OnStylusUp(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
        if (_activeInput != ActiveInputDevice.Stylus)
        {
            return;
        }

        AppendStylusPoints(e.GetStylusPoints(this));
        CompleteActiveStroke();
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_strokeLifecycleOpen && _activeInput == ActiveInputDevice.Mouse)
        {
            CancelActiveStroke(true);
        }
    }

    protected override void OnLostStylusCapture(StylusEventArgs e)
    {
        base.OnLostStylusCapture(e);
        if (_strokeLifecycleOpen && _activeInput == ActiveInputDevice.Stylus)
        {
            CancelActiveStroke(true);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        if (HasActiveStroke)
        {
            CancelActiveStroke(true);
        }

        base.OnRenderSizeChanged(sizeInfo);
    }

    private bool CanStartStroke() =>
        IsEnabled &&
        IsInputEnabled &&
        !HasActiveStroke &&
        (AllowMultipleStrokes || _strokes.Count == 0);

    private bool BeginStroke(ActiveInputDevice inputDevice)
    {
        if (!CanStartStroke())
        {
            return false;
        }

        _activeInput = inputDevice;
        _strokeLifecycleOpen = true;
        _activeStabilizerLevel = StabilizerLevel;
        _activeStabilizer = new StrokeStabilizerV1(_activeStabilizerLevel);
        _activeBrush = CloneAndFreeze(StrokeBrush);
        _activeBrushSize = BrushSize;
        _activeUsesPressure = UsePressure;
        _lastRawTimestamp = double.NegativeInfinity;
        _activeSamples.Clear();
        _activeChunkSamples.Clear();
        _activeVisuals.Clear();
        _redo.Clear();
        StartNewVisualChunk();
        StrokeStarted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void AppendStylusPoints(StylusPointCollection points)
    {
        if (points.Count == 0)
        {
            return;
        }

        var now = GetTimestampSeconds();
        for (var index = 0; index < points.Count; index++)
        {
            var stylusPoint = points[index];
            var proposedTimestamp = now - ((points.Count - index - 1) * AssumedStylusIntervalSeconds);
            var timestamp = double.IsFinite(_lastRawTimestamp)
                ? Math.Max(proposedTimestamp, _lastRawTimestamp + MinimumSampleIntervalSeconds)
                : proposedTimestamp;
            AppendRawSample(
                new Point(stylusPoint.X, stylusPoint.Y),
                Math.Clamp(stylusPoint.PressureFactor, 0, 1),
                timestamp);
        }
    }

    private void AppendRawSample(Point point, double pressure, double timestamp)
    {
        if (_activeStabilizer is null ||
            !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) ||
            Math.Abs(point.X) > MaximumAcceptedCoordinateMagnitude ||
            Math.Abs(point.Y) > MaximumAcceptedCoordinateMagnitude)
        {
            return;
        }

        if (double.IsFinite(_lastRawTimestamp))
        {
            timestamp = Math.Max(timestamp, _lastRawTimestamp + MinimumSampleIntervalSeconds);
        }

        _lastRawTimestamp = timestamp;
        var raw = new StrokeSample(
            new Point2(point.X, point.Y),
            timestamp,
            Math.Clamp(pressure, 0, 1));
        if (!_activeStabilizer.TryProcess(raw, out var stabilized))
        {
            return;
        }

        _activeSamples.Add(stabilized);
        if (_activeSamples.Count > ActiveSampleSoftLimit)
        {
            CompressActiveSamples();
            RebuildActiveVisuals();
        }
        else
        {
            AppendSampleToActiveVisual(stabilized);
        }

        AnswerVersion++;
        RaiseStrokeUpdated(false);
    }

    private void AppendSampleToActiveVisual(StrokeSample sample)
    {
        if (_activeChunkSamples.Count >= SamplesPerVisualChunk)
        {
            var previous = _activeChunkSamples[^1];
            StartNewVisualChunk();
            _activeChunkSamples.Add(previous);
        }

        _activeChunkSamples.Add(sample);
        RenderStrokeChunk(
            _activeVisuals[^1],
            _activeChunkSamples,
            _activeBrush,
            _activeBrushSize,
            _activeUsesPressure);
    }

    private void CompressActiveSamples()
    {
        var tolerance = InitialCompressionToleranceDip;
        List<StrokeSample> compressed;
        do
        {
            compressed = SimplifyRamerDouglasPeucker(_activeSamples, tolerance);
            tolerance *= 2;
        }
        while (compressed.Count > ActiveSampleCompressedTarget && tolerance <= 256);

        if (compressed.Count > ActiveSampleCompressedTarget)
        {
            compressed = SelectImportantSamplesByBucket(
                compressed,
                ActiveSampleCompressedTarget);
        }

        _activeSamples.Clear();
        _activeSamples.AddRange(compressed);
    }

    private void RebuildActiveVisuals()
    {
        RemoveVisuals(_activeVisuals);
        _activeVisuals.Clear();
        _activeChunkSamples.Clear();
        if (_activeSamples.Count == 0)
        {
            return;
        }

        var start = 0;
        while (start < _activeSamples.Count)
        {
            var count = Math.Min(SamplesPerVisualChunk, _activeSamples.Count - start);
            var chunk = _activeSamples.GetRange(start, count);
            var visual = new DrawingVisual();
            _activeVisuals.Add(visual);
            _strokeVisuals.Add(visual);
            RenderStrokeChunk(
                visual,
                chunk,
                _activeBrush,
                _activeBrushSize,
                _activeUsesPressure);

            if (start + count >= _activeSamples.Count)
            {
                _activeChunkSamples.AddRange(chunk);
                break;
            }

            start += count - 1;
        }
    }

    private static List<StrokeSample> SimplifyRamerDouglasPeucker(
        IReadOnlyList<StrokeSample> samples,
        double tolerance)
    {
        if (samples.Count <= 2)
        {
            return samples.ToList();
        }

        var keep = new bool[samples.Count];
        keep[0] = true;
        keep[^1] = true;
        var ranges = new Stack<(int Start, int End)>();
        ranges.Push((0, samples.Count - 1));
        var toleranceSquared = tolerance * tolerance;

        while (ranges.Count > 0)
        {
            var (start, end) = ranges.Pop();
            if (end - start <= 1)
            {
                continue;
            }

            var maximumDistanceSquared = -1.0;
            var maximumIndex = -1;
            var segmentStart = samples[start].Position;
            var segmentEnd = samples[end].Position;
            for (var index = start + 1; index < end; index++)
            {
                var distanceSquared = DistanceToSegmentSquared(
                    samples[index].Position,
                    segmentStart,
                    segmentEnd);
                if (distanceSquared > maximumDistanceSquared)
                {
                    maximumDistanceSquared = distanceSquared;
                    maximumIndex = index;
                }
            }

            if (maximumIndex < 0 || maximumDistanceSquared <= toleranceSquared)
            {
                continue;
            }

            keep[maximumIndex] = true;
            ranges.Push((start, maximumIndex));
            ranges.Push((maximumIndex, end));
        }

        var result = new List<StrokeSample>(samples.Count);
        for (var index = 0; index < samples.Count; index++)
        {
            if (keep[index])
            {
                result.Add(samples[index]);
            }
        }

        return result;
    }

    private static List<StrokeSample> SelectImportantSamplesByBucket(
        IReadOnlyList<StrokeSample> samples,
        int targetCount)
    {
        if (samples.Count <= targetCount)
        {
            return samples.ToList();
        }

        if (targetCount <= 2)
        {
            return [samples[0], samples[^1]];
        }

        var result = new List<StrokeSample>(targetCount) { samples[0] };
        var interiorCount = samples.Count - 2;
        var bucketCount = targetCount - 2;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = 1 + (int)(((long)bucket * interiorCount) / bucketCount);
            var end = 1 + (int)(((long)(bucket + 1) * interiorCount) / bucketCount);
            var anchorStart = samples[start - 1].Position;
            var anchorEnd = samples[Math.Min(samples.Count - 1, end)].Position;
            var selectedIndex = start;
            var maximumDistanceSquared = -1.0;
            for (var index = start; index < end; index++)
            {
                var distanceSquared = DistanceToSegmentSquared(
                    samples[index].Position,
                    anchorStart,
                    anchorEnd);
                if (distanceSquared > maximumDistanceSquared)
                {
                    maximumDistanceSquared = distanceSquared;
                    selectedIndex = index;
                }
            }

            result.Add(samples[selectedIndex]);
        }

        result.Add(samples[^1]);
        return result;
    }

    private static double DistanceToSegmentSquared(Point2 point, Point2 start, Point2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared;
        if (lengthSquared <= 1e-12)
        {
            return (point - start).LengthSquared;
        }

        var projection = Math.Clamp(Point2.Dot(point - start, segment) / lengthSquared, 0, 1);
        var nearest = start + (segment * projection);
        return (point - nearest).LengthSquared;
    }

    private void CompleteActiveStroke()
    {
        if (!_strokeLifecycleOpen)
        {
            return;
        }

        var completedInput = _activeInput;
        _strokeLifecycleOpen = false;
        _activeInput = ActiveInputDevice.None;
        ReleaseCapture(completedInput);

        if (_activeSamples.Count == 0)
        {
            RemoveVisuals(_activeVisuals);
            ResetActiveState();
            StrokeCancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        var stroke = new LogicalStroke(
            _activeSamples.ToArray(),
            _activeStabilizerLevel,
            StrokeStabilizerV1.Version);
        Brush committedBrush = _activeBrush;
        IReadOnlyList<DrawingVisual> committedVisuals = _activeVisuals.ToArray();
        if (RecolorCommittedStrokesOnBrushChange)
        {
            committedBrush = CloneAndFreeze(StrokeBrush);
            committedVisuals = CreateStrokeVisuals(
                stroke.Samples,
                committedBrush,
                _activeBrushSize,
                _activeUsesPressure);
            RemoveVisuals(_activeVisuals);
            foreach (var visual in committedVisuals)
            {
                _strokeVisuals.Add(visual);
            }
        }

        var entry = new StrokeVisualEntry(
            stroke,
            committedVisuals,
            committedBrush,
            _activeBrushSize,
            _activeUsesPressure);
        _strokes.Add(entry);
        ResetActiveState(clearVisualListOnly: true);
        AnswerVersion++;
        StrokesChanged?.Invoke(this, EventArgs.Empty);
        RaiseStrokeUpdated(true);
        StrokeCompleted?.Invoke(this, new StrokeCompletedEventArgs(stroke, _strokes.Count - 1));
    }

    private void CancelActiveStroke(bool raiseEvent)
    {
        if (!_strokeLifecycleOpen)
        {
            return;
        }

        var cancelledInput = _activeInput;
        _strokeLifecycleOpen = false;
        _activeInput = ActiveInputDevice.None;
        ReleaseCapture(cancelledInput);
        RemoveVisuals(_activeVisuals);
        ResetActiveState();
        AnswerVersion++;
        if (raiseEvent)
        {
            StrokeCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetActiveState(bool clearVisualListOnly = false)
    {
        _strokeLifecycleOpen = false;
        _activeInput = ActiveInputDevice.None;
        _activeStabilizer = null;
        _activeSamples.Clear();
        _activeChunkSamples.Clear();
        _activeVisuals.Clear();
        _lastRawTimestamp = double.NegativeInfinity;
        if (!clearVisualListOnly)
        {
            InvalidateVisual();
        }
    }

    private void StartNewVisualChunk()
    {
        _activeChunkSamples.Clear();
        var visual = new DrawingVisual();
        _activeVisuals.Add(visual);
        _strokeVisuals.Add(visual);
    }

    private void ReleaseCapture(ActiveInputDevice inputDevice)
    {
        if (inputDevice == ActiveInputDevice.Mouse && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        else if (inputDevice == ActiveInputDevice.Stylus && IsStylusCaptured)
        {
            ReleaseStylusCapture();
        }
    }

    private void NotifyAnswerChanged()
    {
        AnswerVersion++;
        StrokesChanged?.Invoke(this, EventArgs.Empty);
        RaiseStrokeUpdated(true);
    }

    private void RaiseStrokeUpdated(bool force)
    {
        if (StrokeUpdated is null)
        {
            return;
        }

        var timestamp = GetTimestampSeconds();
        if (!force &&
            double.IsFinite(_lastStrokeUpdatedNotification) &&
            timestamp - _lastStrokeUpdatedNotification < StrokeUpdatedIntervalSeconds)
        {
            return;
        }

        _lastStrokeUpdatedNotification = timestamp;
        StrokeUpdated.Invoke(
            this,
            new StrokeUpdatedEventArgs(GetStrokeSnapshotIncludingActive(), AnswerVersion));
    }

    private void RemoveVisuals(IEnumerable<DrawingVisual> visuals)
    {
        foreach (var visual in visuals)
        {
            _strokeVisuals.Remove(visual);
        }
    }

    private static StrokeVisualReplacement CreateReplacement(
        StrokeVisualEntry entry,
        Brush brush) =>
        new(
            entry,
            CreateStrokeVisuals(
                entry.Stroke.Samples,
                brush,
                entry.BrushSize,
                entry.UsePressure));

    private static IReadOnlyList<DrawingVisual> CreateStrokeVisuals(
        IReadOnlyList<StrokeSample> samples,
        Brush brush,
        double brushSize,
        bool usePressure)
    {
        if (samples.Count == 0)
        {
            return Array.Empty<DrawingVisual>();
        }

        var visuals = new List<DrawingVisual>();
        var start = 0;
        while (start < samples.Count)
        {
            var count = Math.Min(SamplesPerVisualChunk, samples.Count - start);
            var chunk = new StrokeSample[count];
            for (var index = 0; index < count; index++)
            {
                chunk[index] = samples[start + index];
            }

            var visual = new DrawingVisual();
            RenderStrokeChunk(visual, chunk, brush, brushSize, usePressure);
            visuals.Add(visual);
            if (start + count >= samples.Count)
            {
                break;
            }

            // Adjacent DrawingVisual chunks share one endpoint so the displayed
            // stroke remains continuous without duplicating its LogicalStroke.
            start += count - 1;
        }

        return Array.AsReadOnly(visuals.ToArray());
    }

    private static void RenderStrokeChunk(
        DrawingVisual visual,
        IReadOnlyList<StrokeSample> samples,
        Brush brush,
        double brushSize,
        bool usePressure)
    {
        using var context = visual.RenderOpen();
        if (samples.Count == 0)
        {
            return;
        }

        if (samples.Count == 1)
        {
            var sample = samples[0];
            var width = GetDisplayedWidth(brushSize, usePressure, sample.Pressure);
            context.DrawEllipse(
                brush,
                null,
                new Point(sample.Position.X, sample.Position.Y),
                width / 2,
                width / 2);
            return;
        }

        for (var index = 1; index < samples.Count; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            var pressure = (previous.Pressure + current.Pressure) / 2;
            var width = GetDisplayedWidth(brushSize, usePressure, pressure);
            var pen = new Pen(brush, width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            context.DrawLine(
                pen,
                new Point(previous.Position.X, previous.Position.Y),
                new Point(current.Position.X, current.Position.Y));
        }
    }

    private static double GetDisplayedWidth(double brushSize, bool usePressure, double pressure)
    {
        if (!usePressure)
        {
            return brushSize;
        }

        var normalized = Math.Clamp(pressure, 0, 1);
        return brushSize * (0.25 + (0.75 * Math.Sqrt(normalized)));
    }

    private static void DrawTargetCurve(DrawingContext context, TargetCurve curve, Pen pen)
    {
        if (curve.Polyline.Count == 0)
        {
            return;
        }

        if (curve.Polyline.Count == 1)
        {
            var onlyPoint = curve.Polyline[0];
            context.DrawEllipse(
                pen.Brush,
                null,
                new Point(onlyPoint.X, onlyPoint.Y),
                pen.Thickness / 2,
                pen.Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            var first = curve.Polyline[0];
            geometryContext.BeginFigure(new Point(first.X, first.Y), false, false);
            for (var index = 1; index < curve.Polyline.Count; index++)
            {
                var point = curve.Polyline[index];
                geometryContext.LineTo(new Point(point.X, point.Y), true, false);
            }
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawStartHint(DrawingContext context, TargetCurve curve)
    {
        if (curve.Polyline.Count < 2)
        {
            return;
        }

        var startIndex = curve.SuggestedForward ? 0 : curve.Polyline.Count - 1;
        var nextIndex = curve.SuggestedForward ? 1 : curve.Polyline.Count - 2;
        var start = curve.Polyline[startIndex];
        var next = curve.Polyline[nextIndex];
        var direction = (next - start).Normalized();
        var startPoint = new Point(start.X, start.Y);
        context.DrawEllipse(StartHintBrush, null, startPoint, 5, 5);

        if (direction.LengthSquared <= 0)
        {
            return;
        }

        var tip = start + (direction * 22);
        var perpendicular = new Point2(-direction.Y, direction.X);
        var arrowBase = tip - (direction * 7);
        context.DrawLine(StartHintPen, startPoint, new Point(tip.X, tip.Y));
        context.DrawLine(
            StartHintPen,
            new Point(tip.X, tip.Y),
            new Point((arrowBase + (perpendicular * 4)).X, (arrowBase + (perpendicular * 4)).Y));
        context.DrawLine(
            StartHintPen,
            new Point(tip.X, tip.Y),
            new Point((arrowBase - (perpendicular * 4)).X, (arrowBase - (perpendicular * 4)).Y));
    }

    private static bool IsPositiveFiniteDouble(object value) =>
        value is double number && double.IsFinite(number) && number > 0;

    private bool IsInsideCanvas(Point point) =>
        point.X >= 0 && point.X <= ActualWidth && point.Y >= 0 && point.Y <= ActualHeight;

    private bool IsPromotedOrRecentNonMouseEvent(MouseEventArgs args)
    {
        if (args.StylusDevice is not null)
        {
            return true;
        }

        var elapsed = GetTimestampSeconds() - _lastNonMouseInputTimestamp;
        return elapsed is >= 0 and <= PromotedMouseSuppressionSeconds;
    }

    private void RaiseCaptureFailureCancellation()
    {
        // No lifecycle is opened until capture succeeds, so a later LostCapture
        // callback cannot duplicate this notification.
        StrokeCancelled?.Invoke(this, EventArgs.Empty);
    }

    private static double GetTimestampSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static Brush CloneAndFreeze(Brush brush)
    {
        var clone = brush.CloneCurrentValue();
        if (clone.CanFreeze)
        {
            clone.Freeze();
        }

        return clone;
    }

    private static Brush CreateFrozenBrush(uint argb)
    {
        var color = System.Windows.Media.Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static void OnStrokeBrushChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is GeometryStrokeCanvas canvas &&
            canvas.RecolorCommittedStrokesOnBrushChange &&
            args.NewValue is Brush)
        {
            canvas.RecolorCommittedStrokes();
        }
    }

    private static void OnRecolorCommittedStrokesChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is GeometryStrokeCanvas canvas && args.NewValue is true)
        {
            canvas.RecolorCommittedStrokes();
        }
    }

    private static void OnIsInputEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is GeometryStrokeCanvas canvas && args.NewValue is false)
        {
            canvas.CancelActiveStroke(true);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _hostWindow))
        {
            return;
        }

        DetachWindow();
        _hostWindow = window;
        if (_hostWindow is not null)
        {
            _hostWindow.Deactivated += OnHostWindowDeactivated;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelActiveStroke(true);
        DetachWindow();
    }

    private void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CancelActiveStroke(true);
        }
    }

    private void OnHostWindowDeactivated(object? sender, EventArgs e) => CancelActiveStroke(true);

    private void DetachWindow()
    {
        if (_hostWindow is not null)
        {
            _hostWindow.Deactivated -= OnHostWindowDeactivated;
            _hostWindow = null;
        }
    }

    private sealed class StrokeVisualEntry
    {
        public StrokeVisualEntry(
            LogicalStroke stroke,
            IReadOnlyList<DrawingVisual> visuals,
            Brush brush,
            double brushSize,
            bool usePressure)
        {
            Stroke = stroke;
            Visuals = visuals;
            Brush = brush;
            BrushSize = brushSize;
            UsePressure = usePressure;
        }

        public LogicalStroke Stroke { get; }

        public IReadOnlyList<DrawingVisual> Visuals { get; set; }

        public Brush Brush { get; set; }

        public double BrushSize { get; }

        public bool UsePressure { get; }
    }

    private sealed record StrokeVisualReplacement(
        StrokeVisualEntry Entry,
        IReadOnlyList<DrawingVisual> Visuals);

    private enum ActiveInputDevice
    {
        None,
        Mouse,
        Stylus,
    }
}
