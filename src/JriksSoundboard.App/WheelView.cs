using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.App;

/// <summary>What one slice shows.</summary>
public sealed record WheelSlotVisual(string Label, SlotKind Kind, bool IsMissing = false)
{
    public static readonly WheelSlotVisual Empty = new(string.Empty, SlotKind.Empty);
}

/// <summary>
/// Draws a wheel (slices, labels, stick, time-left ring). Used read-only by the overlay and
/// interactively by the editor (click to select, drag sounds in, drag slices to swap).
/// Slice 0 is straight up, then clockwise — same convention as <see cref="WheelSelector"/>.
/// </summary>
public sealed class WheelView : FrameworkElement
{
    public const string SoundDragFormat = "JriksSoundboard.SoundId";
    private const string SlotDragFormat = "JriksSoundboard.SlotIndex";
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string TextFont = "Segoe UI Variable Text, Segoe UI";
    private const string FolderGlyph = "";

    private IReadOnlyList<WheelSlotVisual> slots = [];
    private int? highlightedSlot;
    private int? dropTargetSlot;
    private int? pressedSlot;
    private Point pressPoint;
    private Vector? stick;
    private double deadzoneFraction = 0.22;
    private double? timeRemaining;
    private double dwellProgress;
    private string? centerText;
    private bool isOverlayStyle;
    private Palette? palette;
    private int? hoverSlot;
    private const double MinLabelSize = 8.5;
    private readonly Dictionary<(string Label, double Angle, double Across, double Along, bool Bold), FittedLabel> fittedLabels = [];

    /// <summary>The slice under the mouse changed (editor only); null when the mouse left the wheel.</summary>
    public event Action<int?>? SliceHovered;

    public event Action<int>? SliceClicked;

    public event Action<int>? SliceDoubleClicked;

    public event Action<int, Guid>? SoundDropped;

    public event Action<int, int>? SliceMoved;

    public IReadOnlyList<WheelSlotVisual> Slots
    {
        get => slots;
        set { slots = value ?? []; InvalidateVisual(); }
    }

    public int? HighlightedSlot
    {
        get => highlightedSlot;
        set { if (highlightedSlot != value) { highlightedSlot = value; InvalidateVisual(); } }
    }

    /// <summary>Stick position relative to the outer radius (−1…1), or null to hide it.</summary>
    public Vector? Stick
    {
        get => stick;
        set { if (stick != value) { stick = value; InvalidateVisual(); } }
    }

    public double DeadzoneFraction
    {
        get => deadzoneFraction;
        set { value = Math.Clamp(value, 0.12, 0.6); if (Math.Abs(deadzoneFraction - value) > 0.0001) { deadzoneFraction = value; InvalidateVisual(); } }
    }

    /// <summary>Auto-cancel time left (1 → 0); null hides the ring.</summary>
    public double? TimeRemaining
    {
        get => timeRemaining;
        set { if (timeRemaining != value) { timeRemaining = value; InvalidateVisual(); } }
    }

    /// <summary>How far a folder has filled toward opening (0–1).</summary>
    public double DwellProgress
    {
        get => dwellProgress;
        set { if (Math.Abs(dwellProgress - value) > 0.0001) { dwellProgress = value; InvalidateVisual(); } }
    }

    public string? CenterText
    {
        get => centerText;
        set { if (centerText != value) { centerText = value; InvalidateVisual(); } }
    }

    /// <summary>Dark see-through look for drawing over a game.</summary>
    public bool IsOverlayStyle
    {
        get => isOverlayStyle;
        set { isOverlayStyle = value; palette = null; InvalidateVisual(); }
    }

    /// <summary>Enables clicking, dragging and dropping (editor only).</summary>
    public bool IsEditable
    {
        get => AllowDrop;
        set => AllowDrop = value;
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    private double OuterRadius => Math.Max(10, Math.Min(ActualWidth, ActualHeight) / 2 - 12);

    private double InnerRadius => OuterRadius * deadzoneFraction;

    /// <summary>Which slice is under a point (null in the centre or outside the wheel).</summary>
    public int? SliceAt(Point point)
    {
        Vector v = point - Center;
        double distance = v.Length;
        if (slots.Count < WheelSelector.MinSlices || distance < InnerRadius * 0.85 || distance > OuterRadius + 10)
        {
            return null;
        }
        return WheelSelector.SliceForVector(v.X, v.Y, slots.Count);
    }

    // ───────────────────────── drawing ─────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole square is hit-testable (drops between slices still land).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        int count = slots.Count;
        if (count < WheelSelector.MinSlices)
        {
            return;
        }

        Palette p = palette ??= isOverlayStyle ? Palette.Overlay() : Palette.Editor();
        Point c = Center;
        double outer = OuterRadius;
        double inner = InnerRadius;
        double width = 360.0 / count;
        double gap = count > 12 ? 0.6 : 1.0;

        if (p.Backdrop is not null)
        {
            dc.DrawEllipse(p.Backdrop, null, c, outer + 10, outer + 10);
        }

        for (int i = 0; i < count; i++)
        {
            WheelSlotVisual slot = slots[i];
            bool highlighted = i == highlightedSlot;
            Brush fill = i == dropTargetSlot ? p.Drop
                : highlighted ? p.Highlight
                : i == hoverSlot ? p.Hover
                : slot.Kind == SlotKind.Empty ? p.Empty
                : p.Normal;

            double mid = i * width;
            dc.DrawGeometry(fill, p.Stroke, Wedge(c, inner, outer, mid - width / 2 + gap, mid + width / 2 - gap));

            double labelRadius = (inner + outer) / 2;
            double chord = 2 * labelRadius * Math.Sin(Math.Min(Math.PI / 2, width * Math.PI / 360));
            double maxWidth = Math.Min(outer - inner, chord) * 0.9;
            // Room for a name: across the slice (its width a little inside the label point, where it
            // is narrower) and along it (the ring's depth).
            double across = 2 * (labelRadius - (outer - inner) * 0.22) * Math.Sin(Math.Min(Math.PI / 2, width * Math.PI / 360)) * 0.92;
            double along = (outer - inner) * 0.8;
            DrawSlotLabel(dc, slot, highlighted, PointAt(c, labelRadius, mid), maxWidth, mid, across, along, p);
        }

        dc.DrawEllipse(p.Centre, p.Stroke, c, inner - 3, inner - 3);
        if (!string.IsNullOrEmpty(centerText))
        {
            DrawCenterText(dc, centerText, c, (inner - 3) * 1.6, p);
        }

        if (dwellProgress > 0.001)
        {
            DrawArc(dc, p.DwellPen, c, inner - 3, 360 * dwellProgress);
        }
        if (timeRemaining is double t)
        {
            DrawArc(dc, p.TrackPen, c, outer + 6, 360);
            if (t > 0.001)
            {
                DrawArc(dc, p.TimerPen, c, outer + 6, 360 * t);
            }
        }
        // The dot only appears once it leaves the centre (cancel) circle, so it never covers the
        // wheel/folder name written there.
        if (stick is Vector s && s.Length * outer > inner - 3)
        {
            dc.DrawEllipse(p.StickBrush, p.StickPen, c + s * outer, 6.5, 6.5);
        }
    }

    private void DrawSlotLabel(DrawingContext dc, WheelSlotVisual slot, bool highlighted, Point at, double maxWidth,
        double angle, double across, double along, Palette p)
    {
        double size = isOverlayStyle ? 13 : 12;
        Brush brush = highlighted ? p.TextOnHighlight : slot.IsMissing ? p.Caution : p.Text;

        switch (slot.Kind)
        {
            case SlotKind.Empty:
                if (!isOverlayStyle)
                {
                    FormattedText plus = Text("+", 18, p.Hint, FontWeights.Normal, maxWidth, 1, TextFont);
                    dc.DrawText(plus, new Point(at.X - maxWidth / 2, at.Y - plus.Height / 2));
                }
                break;

            case SlotKind.Folder:
                FormattedText icon = Text(FolderGlyph, size + 5, brush, FontWeights.Normal, maxWidth, 1, IconFont);
                FormattedText name = Text(slot.Label, size, brush, FontWeights.SemiBold, maxWidth, 1, TextFont);
                double top = at.Y - (icon.Height + name.Height) / 2;
                dc.DrawText(icon, new Point(at.X - maxWidth / 2, top));
                dc.DrawText(name, new Point(at.X - maxWidth / 2, top + icon.Height));
                break;

            default:
                string label = slot.IsMissing ? "⚠ " + slot.Label : slot.Label;
                FormattedText text = FitLabel(label, size, brush, highlighted ? FontWeights.SemiBold : FontWeights.Normal, angle, across, along);
                dc.DrawText(text, new Point(at.X - text.MaxTextWidth / 2, at.Y - text.Height / 2));
                break;
        }
    }

    /// <summary>
    /// Makes as much of a long name visible as possible: shrinks the font (down to 8.5), lets it
    /// wrap at spaces, underscores, dashes and dots, and breaks a part mid-word only if it still
    /// doesn't fit. Only after all that is the name cut with "…". Results are cached per name and
    /// slice size, so redrawing the overlay every frame stays cheap.
    /// </summary>
    private FormattedText FitLabel(string label, double baseSize, Brush brush, FontWeight weight, double angleDegrees, double across, double along)
    {
        // The text box is upright, but the slice points at an angle. An upright w×h box centred on
        // the label point stays inside the slice when its extent across the slice (w·cos + h·sin)
        // and along it (w·sin + h·cos) fit — so side slices get wide, short boxes and top/bottom
        // slices tall, narrow ones, and long names never spill into the neighbours.
        double radians = angleDegrees * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(radians));
        double sin = Math.Abs(Math.Sin(radians));

        double WidestFor(double height) => Math.Max(height, Math.Min(
            cos > 0.001 ? (across - height * sin) / cos : double.MaxValue,
            sin > 0.001 ? (along - height * cos) / sin : double.MaxValue));
        double TallestFor(double boxWidth) => Math.Min(
            sin > 0.001 ? (across - boxWidth * cos) / sin : double.MaxValue,
            cos > 0.001 ? (along - boxWidth * sin) / cos : double.MaxValue);

        var key = (label, Math.Round(angleDegrees), Math.Round(across), Math.Round(along), weight == FontWeights.SemiBold);
        if (!fittedLabels.TryGetValue(key, out FittedLabel fitted))
        {
            bool found = false;
            string wrappable = WithBreaks(label, null);
            for (double size = baseSize; size >= MinLabelSize && !found; size -= 0.5)
            {
                double widest = WidestFor(size * 1.34);
                double step = Math.Max(2, widest / 10);
                for (double boxWidth = widest; boxWidth >= size * 3 && !found; boxWidth -= step)
                {
                    if (LabelParts(label).Any(part => WidthOf(part, size, weight) > boxWidth))
                    {
                        break; // narrower boxes would only make this worse
                    }
                    double height = Text(wrappable, size, Brushes.White, weight, boxWidth, int.MaxValue, TextFont).Height;
                    if (height <= TallestFor(boxWidth) + 0.5)
                    {
                        fitted = new FittedLabel(size, wrappable, boxWidth, int.MaxValue);
                        found = true;
                    }
                }
            }

            if (!found)
            {
                // Still too long: smallest size, the box shape that shows the most text (e.g. two short
                // lines instead of one long one on diagonal slices), parts broken mid-word — then "…".
                double lineHeight = MinLabelSize * 1.34;
                double bestWidth = WidestFor(lineHeight);
                int bestLines = Math.Max(1, (int)(TallestFor(bestWidth) / lineHeight));
                for (double boxWidth = bestWidth; boxWidth >= MinLabelSize * 3; boxWidth -= 2)
                {
                    int lines = Math.Max(1, (int)(TallestFor(boxWidth) / lineHeight));
                    if (lines * boxWidth > bestLines * bestWidth)
                    {
                        bestWidth = boxWidth;
                        bestLines = lines;
                    }
                }
                double chosenWidth = bestWidth;
                fitted = new FittedLabel(MinLabelSize, WithBreaks(label, part => WidthOf(part, MinLabelSize, weight) > chosenWidth), chosenWidth, bestLines);
            }

            if (fittedLabels.Count > 1000)
            {
                fittedLabels.Clear();
            }
            fittedLabels[key] = fitted;
        }

        return Text(fitted.Text, fitted.Size, brush, weight, fitted.Width, fitted.MaxLines, TextFont);
    }

    private readonly record struct FittedLabel(double Size, string Text, double Width, int MaxLines);

    /// <summary>Splits a name after spaces, underscores, dashes, dots and slashes (separator stays with the part).</summary>
    private static List<string> LabelParts(string label)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] is ' ' or '_' or '-' or '.' or '/')
            {
                parts.Add(label[start..(i + 1)]);
                start = i + 1;
            }
        }
        if (start < label.Length)
        {
            parts.Add(label[start..]);
        }
        return parts;
    }

    /// <summary>Adds invisible break opportunities after separators (and inside parts that match <paramref name="breakEverywhere"/>).</summary>
    private static string WithBreaks(string label, Func<string, bool>? breakEverywhere)
    {
        const char zeroWidthSpace = '​';
        var builder = new System.Text.StringBuilder(label.Length * 2);
        foreach (string part in LabelParts(label))
        {
            if (breakEverywhere?.Invoke(part) == true)
            {
                foreach (char ch in part)
                {
                    builder.Append(ch).Append(zeroWidthSpace);
                }
            }
            else
            {
                builder.Append(part);
                if (part[^1] is '_' or '-' or '.' or '/')
                {
                    builder.Append(zeroWidthSpace);
                }
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// The centre circle is small, so shrink the font until the longest word fits on one line
    /// (otherwise "Cancelled" would be broken mid-word and read as "Cancell").
    /// </summary>
    private void DrawCenterText(DrawingContext dc, string value, Point c, double maxWidth, Palette p)
    {
        const double minSize = 8;
        double size = isOverlayStyle ? 13 : 12;
        string[] words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        while (size > minSize && words.Max(w => WidthOf(w, size, FontWeights.SemiBold)) > maxWidth)
        {
            size -= 0.5;
        }

        FormattedText text = Text(value, size, p.Text, FontWeights.SemiBold, maxWidth, 2, TextFont);
        dc.DrawText(text, new Point(c.X - maxWidth / 2, c.Y - text.Height / 2));
    }

    private double WidthOf(string word, double size, FontWeight weight) =>
        new FormattedText(word, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(TextFont), FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private FormattedText Text(string text, double size, Brush brush, FontWeight weight, double maxWidth, int maxLines, string font) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private static Point PointAt(Point c, double radius, double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        return new Point(c.X + radius * Math.Sin(a), c.Y - radius * Math.Cos(a));
    }

    private static Geometry Wedge(Point c, double inner, double outer, double startDeg, double endDeg)
    {
        bool large = endDeg - startDeg > 180;
        var figure = new PathFigure { StartPoint = PointAt(c, inner, startDeg), IsClosed = true };
        figure.Segments.Add(new LineSegment(PointAt(c, outer, startDeg), true));
        figure.Segments.Add(new ArcSegment(PointAt(c, outer, endDeg), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(PointAt(c, inner, endDeg), true));
        figure.Segments.Add(new ArcSegment(PointAt(c, inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Arc starting at 12 o'clock going clockwise.</summary>
    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double radius, double sweepDegrees)
    {
        if (sweepDegrees >= 359.9)
        {
            dc.DrawEllipse(null, pen, c, radius, radius);
            return;
        }
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(PointAt(c, radius, 0), false, false);
            ctx.ArcTo(PointAt(c, radius, sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    // ───────────────────────── editing ─────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEditable || SliceAt(e.GetPosition(this)) is not int index)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            pressedSlot = null;
            SliceDoubleClicked?.Invoke(index);
        }
        else
        {
            pressedSlot = index;
            pressPoint = e.GetPosition(this);
            SliceClicked?.Invoke(index);
        }
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null);
    }

    private void SetHover(int? index)
    {
        if (hoverSlot == index)
        {
            return;
        }
        hoverSlot = index;
        InvalidateVisual();
        SliceHovered?.Invoke(index);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsEditable)
        {
            SetHover(SliceAt(e.GetPosition(this)));
        }
        if (pressedSlot is not int from || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - pressPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        pressedSlot = null;
        if (from < slots.Count && slots[from].Kind != SlotKind.Empty)
        {
            DragDrop.DoDragDrop(this, new DataObject(SlotDragFormat, from), DragDropEffects.Move);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        pressedSlot = null;
    }

    protected override void OnDragEnter(DragEventArgs e) => HandleDragOver(e);

    protected override void OnDragOver(DragEventArgs e) => HandleDragOver(e);

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        SetDropTarget(null);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        SetDropTarget(null);
        if (!IsEditable || SliceAt(e.GetPosition(this)) is not int index)
        {
            return;
        }

        if (e.Data.GetData(SoundDragFormat) is string id && Guid.TryParse(id, out Guid soundId))
        {
            SoundDropped?.Invoke(index, soundId);
        }
        else if (e.Data.GetData(SlotDragFormat) is int from && from != index)
        {
            SliceMoved?.Invoke(from, index);
        }
        e.Handled = true;
    }

    private void HandleDragOver(DragEventArgs e)
    {
        int? index = IsEditable ? SliceAt(e.GetPosition(this)) : null;
        bool isSlot = e.Data.GetDataPresent(SlotDragFormat);
        bool accepted = index is not null && (isSlot || e.Data.GetDataPresent(SoundDragFormat));
        e.Effects = !accepted ? DragDropEffects.None : isSlot ? DragDropEffects.Move : DragDropEffects.Copy;
        SetDropTarget(accepted ? index : null);
        e.Handled = true;
    }

    private void SetDropTarget(int? index)
    {
        if (dropTargetSlot != index)
        {
            dropTargetSlot = index;
            InvalidateVisual();
        }
    }

    // ───────────────────────── colours ─────────────────────────

    private sealed record Palette(
        Brush? Backdrop, Brush Normal, Brush Empty, Brush Hover, Brush Highlight, Brush Drop, Pen Stroke,
        Brush Text, Brush TextOnHighlight, Brush Caution, Brush Hint, Brush Centre,
        Brush StickBrush, Pen StickPen, Pen TimerPen, Pen TrackPen, Pen DwellPen)
    {
        public static Palette Overlay()
        {
            Brush accent = Resource("AccentFillColorDefaultBrush", Color.FromRgb(0x60, 0xA5, 0xFA));
            return new Palette(
                Backdrop: Solid(Color.FromArgb(0xD9, 0x12, 0x12, 0x16)),
                Normal: Solid(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Empty: Solid(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                Hover: Solid(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                Highlight: accent,
                Drop: accent,
                Stroke: Pen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1),
                Text: Brushes.White,
                TextOnHighlight: Resource("TextOnAccentFillColorPrimaryBrush", Colors.Black),
                Caution: Resource("SystemFillColorCautionBrush", Colors.Orange),
                Hint: Solid(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                Centre: Solid(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)),
                StickBrush: Brushes.White,
                StickPen: Pen(Color.FromArgb(0x80, 0x00, 0x00, 0x00), 1.5),
                TimerPen: Pen(accent, 4, round: true),
                TrackPen: Pen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 4),
                DwellPen: Pen(accent, 3, round: true));
        }

        public static Palette Editor()
        {
            Brush accent = Resource("AccentFillColorDefaultBrush", Color.FromRgb(0x25, 0x63, 0xEB));
            Brush text = Resource("TextFillColorPrimaryBrush", Colors.White);
            return new Palette(
                Backdrop: null,
                Normal: Solid(Color.FromArgb(0x38, 0x80, 0x80, 0x80)),
                Empty: Solid(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
                Hover: Solid(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                Highlight: accent,
                Drop: Resource("AccentFillColorSecondaryBrush", Color.FromRgb(0x60, 0xA5, 0xFA)),
                Stroke: Pen(Color.FromArgb(0x70, 0x80, 0x80, 0x80), 1),
                Text: text,
                TextOnHighlight: Resource("TextOnAccentFillColorPrimaryBrush", Colors.White),
                Caution: Resource("SystemFillColorCautionBrush", Colors.Orange),
                Hint: Resource("TextFillColorTertiaryBrush", Colors.Gray),
                Centre: Solid(Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                StickBrush: text,
                StickPen: Pen(Colors.Transparent, 0),
                TimerPen: Pen(accent, 4, round: true),
                TrackPen: Pen(Color.FromArgb(0x30, 0x80, 0x80, 0x80), 4),
                DwellPen: Pen(accent, 3, round: true));
        }

        // Theme brushes track the system theme and can't be frozen, so copy their colour into a
        // frozen brush we own (pens built from them are frozen too, for fast rendering).
        private static Brush Resource(string key, Color fallback) =>
            Solid(Application.Current?.TryFindResource(key) switch
            {
                SolidColorBrush solid => solid.Color,
                Color color => color,
                _ => fallback,
            });

        private static Brush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen Pen(Color color, double thickness) => Pen(Solid(color), thickness);

        private static Pen Pen(Brush brush, double thickness, bool round = false)
        {
            var pen = new Pen(brush, thickness);
            if (round)
            {
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
            }
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }
            return pen;
        }
    }
}
