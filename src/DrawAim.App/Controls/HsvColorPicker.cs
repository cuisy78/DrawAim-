using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawAim.Core.Color;

namespace DrawAim.App.Controls;

public sealed class HsvColorChangedEventArgs : EventArgs
{
    public HsvColorChangedEventArgs(HsvColor hsv, SrgbColor color)
    {
        Hsv = hsv;
        Color = color;
    }

    public HsvColor Hsv { get; }

    public SrgbColor Color { get; }

    public System.Windows.Media.Color MediaColor => System.Windows.Media.Color.FromRgb(
        ToByte(Color.R),
        ToByte(Color.G),
        ToByte(Color.B));

    private static byte ToByte(double channel) =>
        (byte)Math.Round(Math.Clamp(channel, 0, 1) * byte.MaxValue);
}

/// <summary>
/// Interactive hue ring and HSV saturation/value square. Values are always
/// finite, normalized and converted through DrawAim.Core.Color.ColorMath.
/// </summary>
public sealed class HsvColorPicker : FrameworkElement
{
    private const int HueSegments = 180;
    private const double RingWidthRatio = 0.18;
    private const double MinimumRingWidth = 18;
    private const double MaximumRingWidth = 34;
    private const double PromotedMouseSuppressionSeconds = 0.08;

    private static readonly HsvColor DefaultHsv = new(210, 0.65, 0.82);
    private static readonly SrgbColor DefaultColor = ColorMath.HsvToSrgb(DefaultHsv);
    private static readonly Brush CheckerBackground = CreateFrozenBrush(0xFF20252E);
    private static readonly Pen OuterBorderPen = CreateFrozenPen(0x66000000, 1.5);
    private static readonly Pen InnerBorderPen = CreateFrozenPen(0x99FFFFFF, 1.0);
    private static readonly Pen WhiteMarkerPen = CreateFrozenPen(0xFFFFFFFF, 3.0);
    private static readonly Pen BlackMarkerPen = CreateFrozenPen(0xCC000000, 1.2);

    public static readonly DependencyProperty SelectedHsvProperty = DependencyProperty.Register(
        nameof(SelectedHsv),
        typeof(HsvColor),
        typeof(HsvColorPicker),
        new FrameworkPropertyMetadata(
            DefaultHsv,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedHsvChanged,
            CoerceSelectedHsv));

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor),
        typeof(SrgbColor),
        typeof(HsvColorPicker),
        new FrameworkPropertyMetadata(
            DefaultColor,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedColorChanged,
            CoerceSelectedColor));

    private bool _synchronizingProperties;
    private PickerZone _activeZone;
    private DrawingGroup? _ringDrawing;
    private WriteableBitmap? _svBitmap;
    private byte[]? _svPixels;
    private Size _cachedRenderSize;
    private double _cachedHue = double.NaN;
    private double _cachedDpiScaleX = double.NaN;
    private double _cachedDpiScaleY = double.NaN;
    private Point _center;
    private Rect _squareRect;
    private double _outerRadius;
    private double _innerRadius;
    private double _lastNonMouseInputTimestamp = double.NegativeInfinity;

    public HsvColorPicker()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        MinWidth = 180;
        MinHeight = 180;
        ClipToBounds = true;
        IsEnabledChanged += OnPickerEnabledChanged;
    }

    public event EventHandler<HsvColorChangedEventArgs>? SelectedColorChanged;

    public HsvColor SelectedHsv
    {
        get => (HsvColor)GetValue(SelectedHsvProperty);
        set => SetValue(SelectedHsvProperty, value);
    }

    public SrgbColor SelectedColor
    {
        get => (SrgbColor)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public System.Windows.Media.Color SelectedMediaColor => System.Windows.Media.Color.FromRgb(
        ToByte(SelectedColor.R),
        ToByte(SelectedColor.G),
        ToByte(SelectedColor.B));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 280 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 280 : availableSize.Height;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (!TryCalculateLayout())
        {
            return;
        }

        EnsureRingDrawing();
        EnsureSaturationValueBitmap();

        drawingContext.DrawEllipse(CheckerBackground, null, _center, _outerRadius, _outerRadius);
        if (_ringDrawing is not null)
        {
            drawingContext.DrawDrawing(_ringDrawing);
        }

        drawingContext.DrawEllipse(null, OuterBorderPen, _center, _outerRadius, _outerRadius);
        drawingContext.DrawEllipse(null, InnerBorderPen, _center, _innerRadius, _innerRadius);
        if (_svBitmap is not null)
        {
            drawingContext.DrawImage(_svBitmap, _squareRect);
        }

        drawingContext.DrawRectangle(null, OuterBorderPen, _squareRect);
        DrawHueMarker(drawingContext);
        DrawSaturationValueMarker(drawingContext);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        InvalidateCaches();
        base.OnRenderSizeChanged(sizeInfo);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsPromotedOrRecentNonMouseEvent(e) || !IsEnabled)
        {
            return;
        }

        var zone = HitTestZone(e.GetPosition(this));
        if (zone == PickerZone.None)
        {
            return;
        }

        Focus();
        e.Handled = true;
        if (!CaptureMouse() || !IsMouseCaptured)
        {
            _activeZone = PickerZone.None;
            return;
        }

        _activeZone = zone;
        UpdateSelection(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_activeZone == PickerZone.None || IsPromotedOrRecentNonMouseEvent(e))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMouseInteraction();
            return;
        }

        UpdateSelection(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_activeZone == PickerZone.None || IsPromotedOrRecentNonMouseEvent(e))
        {
            return;
        }

        UpdateSelection(e.GetPosition(this));
        EndMouseInteraction();
        e.Handled = true;
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        base.OnStylusDown(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        if (!IsEnabled ||
            e.StylusDevice.Inverted ||
            e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus)
        {
            e.Handled = true;
            return;
        }

        var zone = HitTestZone(e.GetPosition(this));
        if (zone == PickerZone.None)
        {
            e.Handled = true;
            return;
        }

        Focus();
        e.Handled = true;
        if (!CaptureStylus() || !IsStylusCaptured)
        {
            _activeZone = PickerZone.None;
            return;
        }

        _activeZone = zone;
        UpdateSelection(e.GetPosition(this));
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        base.OnStylusMove(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
        if (_activeZone == PickerZone.None || !IsStylusCaptured)
        {
            return;
        }

        UpdateSelection(e.GetPosition(this));
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        base.OnStylusUp(e);
        _lastNonMouseInputTimestamp = GetTimestampSeconds();
        e.Handled = true;
        if (_activeZone == PickerZone.None || !IsStylusCaptured)
        {
            return;
        }

        UpdateSelection(e.GetPosition(this));
        _activeZone = PickerZone.None;
        ReleaseStylusCapture();
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
        if (!IsStylusCaptured)
        {
            _activeZone = PickerZone.None;
        }
    }

    protected override void OnLostStylusCapture(StylusEventArgs e)
    {
        base.OnLostStylusCapture(e);
        if (!IsMouseCaptured)
        {
            _activeZone = PickerZone.None;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var hsv = SelectedHsv;
        var fineStep = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.0025 : 0.01;
        HsvColor? next = e.Key switch
        {
            Key.Left => hsv with { Saturation = hsv.Saturation - fineStep },
            Key.Right => hsv with { Saturation = hsv.Saturation + fineStep },
            Key.Up => hsv with { Value = hsv.Value + fineStep },
            Key.Down => hsv with { Value = hsv.Value - fineStep },
            Key.PageUp => hsv with { HueDegrees = hsv.HueDegrees + (fineStep * 100) },
            Key.PageDown => hsv with { HueDegrees = hsv.HueDegrees - (fineStep * 100) },
            _ => null,
        };

        if (next.HasValue)
        {
            SetCurrentValue(SelectedHsvProperty, next.Value);
            e.Handled = true;
        }
    }

    private bool TryCalculateLayout()
    {
        var minimumDimension = Math.Min(ActualWidth, ActualHeight);
        if (!double.IsFinite(minimumDimension) || minimumDimension < 40)
        {
            return false;
        }

        _center = new Point(ActualWidth / 2, ActualHeight / 2);
        _outerRadius = Math.Max(0, (minimumDimension / 2) - 5);
        var ringWidth = Math.Clamp(
            _outerRadius * RingWidthRatio,
            MinimumRingWidth,
            MaximumRingWidth);
        _innerRadius = Math.Max(1, _outerRadius - ringWidth);
        var squareHalfSize = Math.Max(1, (_innerRadius - 7) / Math.Sqrt(2));
        _squareRect = new Rect(
            _center.X - squareHalfSize,
            _center.Y - squareHalfSize,
            squareHalfSize * 2,
            squareHalfSize * 2);
        return true;
    }

    private void EnsureRingDrawing()
    {
        if (_ringDrawing is not null && _cachedRenderSize == RenderSize)
        {
            return;
        }

        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            for (var index = 0; index < HueSegments; index++)
            {
                var startHue = index * (360.0 / HueSegments);
                var endHue = (index + 1) * (360.0 / HueSegments);
                var geometry = CreateRingSegment(startHue - 0.15, endHue + 0.15);
                var color = ColorMath.HsvToSrgb(new HsvColor((startHue + endHue) / 2, 1, 1));
                var brush = new SolidColorBrush(ToMediaColor(color));
                brush.Freeze();
                context.DrawGeometry(brush, null, geometry);
            }
        }

        group.Freeze();
        _ringDrawing = group;
        _cachedRenderSize = RenderSize;
    }

    private StreamGeometry CreateRingSegment(double startDegrees, double endDegrees)
    {
        var startRadians = startDegrees * Math.PI / 180;
        var endRadians = endDegrees * Math.PI / 180;
        var outerStart = PointOnCircle(startRadians, _outerRadius);
        var outerEnd = PointOnCircle(endRadians, _outerRadius);
        var innerEnd = PointOnCircle(endRadians, _innerRadius);
        var innerStart = PointOnCircle(startRadians, _innerRadius);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(outerStart, true, true);
            context.LineTo(outerEnd, true, false);
            context.LineTo(innerEnd, true, false);
            context.LineTo(innerStart, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void EnsureSaturationValueBitmap()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_svBitmap is not null &&
            Math.Abs(_cachedHue - SelectedHsv.HueDegrees) < 0.0001 &&
            Math.Abs(_cachedDpiScaleX - dpi.DpiScaleX) < 0.0001 &&
            Math.Abs(_cachedDpiScaleY - dpi.DpiScaleY) < 0.0001 &&
            _cachedRenderSize == RenderSize)
        {
            return;
        }

        var pixelWidth = Math.Clamp(
            (int)Math.Ceiling(_squareRect.Width * dpi.DpiScaleX),
            64,
            512);
        var pixelHeight = Math.Clamp(
            (int)Math.Ceiling(_squareRect.Height * dpi.DpiScaleY),
            64,
            512);
        var stride = pixelWidth * 4;
        if (_svBitmap is null ||
            _svBitmap.PixelWidth != pixelWidth ||
            _svBitmap.PixelHeight != pixelHeight)
        {
            _svBitmap = new WriteableBitmap(
                pixelWidth,
                pixelHeight,
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Bgra32,
                null);
            _svPixels = new byte[stride * pixelHeight];
        }

        var pixels = _svPixels ?? throw new InvalidOperationException("SV pixel buffer was not initialized.");
        for (var x = 0; x < pixelWidth; x++)
        {
            var saturation = x / (double)Math.Max(1, pixelWidth - 1);
            var fullValueColor = ColorMath.HsvToSrgb(
                new HsvColor(SelectedHsv.HueDegrees, saturation, 1));
            for (var y = 0; y < pixelHeight; y++)
            {
                var value = 1 - (y / (double)Math.Max(1, pixelHeight - 1));
                var offset = (y * stride) + (x * 4);
                pixels[offset] = ToByte(fullValueColor.B * value);
                pixels[offset + 1] = ToByte(fullValueColor.G * value);
                pixels[offset + 2] = ToByte(fullValueColor.R * value);
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        _svBitmap.WritePixels(
            new Int32Rect(0, 0, pixelWidth, pixelHeight),
            pixels,
            stride,
            0);
        _cachedHue = SelectedHsv.HueDegrees;
        _cachedDpiScaleX = dpi.DpiScaleX;
        _cachedDpiScaleY = dpi.DpiScaleY;
        _cachedRenderSize = RenderSize;
    }

    private void DrawHueMarker(DrawingContext context)
    {
        var angle = SelectedHsv.HueDegrees * Math.PI / 180;
        var radius = (_outerRadius + _innerRadius) / 2;
        var marker = PointOnCircle(angle, radius);
        context.DrawEllipse(null, WhiteMarkerPen, marker, 7, 7);
        context.DrawEllipse(null, BlackMarkerPen, marker, 5, 5);
    }

    private void DrawSaturationValueMarker(DrawingContext context)
    {
        var marker = new Point(
            _squareRect.Left + (SelectedHsv.Saturation * _squareRect.Width),
            _squareRect.Top + ((1 - SelectedHsv.Value) * _squareRect.Height));
        var fill = new SolidColorBrush(ToMediaColor(SelectedColor));
        fill.Freeze();
        context.DrawEllipse(fill, WhiteMarkerPen, marker, 7, 7);
        context.DrawEllipse(null, BlackMarkerPen, marker, 5, 5);
    }

    private PickerZone HitTestZone(Point point)
    {
        if (!TryCalculateLayout())
        {
            return PickerZone.None;
        }

        if (_squareRect.Contains(point))
        {
            return PickerZone.SaturationValue;
        }

        var delta = point - _center;
        var radius = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
        return radius >= _innerRadius && radius <= _outerRadius
            ? PickerZone.Hue
            : PickerZone.None;
    }

    private void UpdateSelection(Point point)
    {
        var current = SelectedHsv;
        if (_activeZone == PickerZone.Hue)
        {
            var angle = Math.Atan2(point.Y - _center.Y, point.X - _center.X) * 180 / Math.PI;
            SetCurrentValue(
                SelectedHsvProperty,
                current with { HueDegrees = ColorMath.NormalizeHue(angle) });
        }
        else if (_activeZone == PickerZone.SaturationValue)
        {
            var saturation = (point.X - _squareRect.Left) / _squareRect.Width;
            var value = 1 - ((point.Y - _squareRect.Top) / _squareRect.Height);
            SetCurrentValue(
                SelectedHsvProperty,
                current with
                {
                    Saturation = Math.Clamp(saturation, 0, 1),
                    Value = Math.Clamp(value, 0, 1),
                });
        }
    }

    private void EndMouseInteraction()
    {
        _activeZone = PickerZone.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private Point PointOnCircle(double angleRadians, double radius) => new(
        _center.X + (Math.Cos(angleRadians) * radius),
        _center.Y + (Math.Sin(angleRadians) * radius));

    private void InvalidateCaches()
    {
        _ringDrawing = null;
        _svBitmap = null;
        _svPixels = null;
        _cachedRenderSize = default;
        _cachedHue = double.NaN;
        InvalidateVisual();
    }

    private void RaiseSelectedColorChanged()
    {
        SelectedColorChanged?.Invoke(
            this,
            new HsvColorChangedEventArgs(SelectedHsv, SelectedColor));
    }

    private static object CoerceSelectedHsv(DependencyObject dependencyObject, object value)
    {
        var hsv = value is HsvColor candidate ? candidate : DefaultHsv;
        return new HsvColor(
            ColorMath.NormalizeHue(hsv.HueDegrees),
            double.IsFinite(hsv.Saturation) ? Math.Clamp(hsv.Saturation, 0, 1) : DefaultHsv.Saturation,
            double.IsFinite(hsv.Value) ? Math.Clamp(hsv.Value, 0, 1) : DefaultHsv.Value);
    }

    private static object CoerceSelectedColor(DependencyObject dependencyObject, object value)
    {
        var color = value is SrgbColor candidate && candidate.IsFinite
            ? candidate
            : DefaultColor;
        return color.Clamp();
    }

    private static void OnSelectedHsvChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var picker = (HsvColorPicker)dependencyObject;
        if (picker._synchronizingProperties)
        {
            return;
        }

        picker._synchronizingProperties = true;
        picker.SetCurrentValue(
            SelectedColorProperty,
            ColorMath.HsvToSrgb((HsvColor)args.NewValue));
        picker._synchronizingProperties = false;
        picker._cachedHue = double.NaN;
        picker.InvalidateVisual();
        picker.RaiseSelectedColorChanged();
    }

    private static void OnSelectedColorChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var picker = (HsvColorPicker)dependencyObject;
        if (picker._synchronizingProperties)
        {
            return;
        }

        picker._synchronizingProperties = true;
        picker.SetCurrentValue(
            SelectedHsvProperty,
            ColorMath.SrgbToHsv((SrgbColor)args.NewValue));
        picker._synchronizingProperties = false;
        picker._cachedHue = double.NaN;
        picker.InvalidateVisual();
        picker.RaiseSelectedColorChanged();
    }

    private static System.Windows.Media.Color ToMediaColor(SrgbColor color) =>
        System.Windows.Media.Color.FromRgb(ToByte(color.R), ToByte(color.G), ToByte(color.B));

    private static byte ToByte(double channel) =>
        (byte)Math.Round(Math.Clamp(channel, 0, 1) * byte.MaxValue);

    private bool IsPromotedOrRecentNonMouseEvent(MouseEventArgs args)
    {
        if (args.StylusDevice is not null)
        {
            return true;
        }

        var elapsed = GetTimestampSeconds() - _lastNonMouseInputTimestamp;
        return elapsed is >= 0 and <= PromotedMouseSuppressionSeconds;
    }

    private static double GetTimestampSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

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

    private static Pen CreateFrozenPen(uint argb, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(argb), thickness);
        pen.Freeze();
        return pen;
    }

    private void OnPickerEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false)
        {
            return;
        }

        _activeZone = PickerZone.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (IsStylusCaptured)
        {
            ReleaseStylusCapture();
        }
    }

    private enum PickerZone
    {
        None,
        Hue,
        SaturationValue,
    }
}
