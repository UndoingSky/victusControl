using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VictusControl.App.Controls;

/// <summary>
/// A circular percentage gauge: a full track ring with an accent arc swept over
/// it, and the value set in the middle. Drawn as geometry rather than composed
/// from images so it stays crisp at any size and any DPI.
/// </summary>
public class RingGauge : Control {

    // No control template: everything this draws comes from OnRender, so there is
    // no Generic.xaml default style to keep in sync.

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty =
        DependencyProperty.Register(nameof(ArcBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(Brushes.Magenta, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(RingGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(RingGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string DisplayText { get => (string)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }

    // The rendered sweep trails the real value slightly, so a reading that jumps
    // from 4% to 90% travels there instead of snapping.
    private static readonly DependencyProperty RenderedValueProperty =
        DependencyProperty.Register(nameof(RenderedValue), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double RenderedValue {
        get => (double)GetValue(RenderedValueProperty);
        set => SetValue(RenderedValueProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is not RingGauge g) return;
        var animation = new DoubleAnimation {
            To = g.Value,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        g.BeginAnimation(RenderedValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc) {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double t = Thickness;
        double radius = (Math.Min(w, h) - t) / 2.0;
        if (radius <= 0) return;

        var centre = new Point(w / 2.0, h / 2.0);

        var trackPen = new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, centre, radius, radius);

        double max = Maximum <= 0 ? 100 : Maximum;
        double fraction = Math.Clamp(RenderedValue / max, 0, 1);
        if (fraction <= 0.0001) {
            DrawText(dc, centre);
            return;
        }

        var arcPen = new Pen(ArcBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (fraction >= 0.9999) {
            dc.DrawEllipse(null, arcPen, centre, radius, radius);
        } else {
            // Start at twelve o'clock and sweep clockwise
            double angle = fraction * 360.0;
            var start = new Point(centre.X, centre.Y - radius);
            double rad = (angle - 90) * Math.PI / 180.0;
            var end = new Point(centre.X + radius * Math.Cos(rad), centre.Y + radius * Math.Sin(rad));

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment {
                Point = end,
                Size = new Size(radius, radius),
                IsLargeArc = angle > 180,
                SweepDirection = SweepDirection.Clockwise
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            dc.DrawGeometry(null, arcPen, geometry);
        }

        DrawText(dc, centre);
    }

    private void DrawText(DrawingContext dc, Point centre) {
        var dpi = VisualTreeHelper.GetDpi(this);
        string value = string.IsNullOrEmpty(DisplayText)
            ? $"{Value:0}%"
            : DisplayText;

        var valueText = new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            FontSize, Foreground, dpi.PixelsPerDip);

        double totalHeight = valueText.Height;
        FormattedText? captionText = null;
        if (!string.IsNullOrEmpty(Caption)) {
            captionText = new FormattedText(Caption, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                12, (Brush)GetValue(CaptionBrushProperty) ?? Foreground, dpi.PixelsPerDip);
            totalHeight += captionText.Height + 2;
        }

        double y = centre.Y - totalHeight / 2.0;
        dc.DrawText(valueText, new Point(centre.X - valueText.Width / 2.0, y));

        if (captionText != null) {
            dc.DrawText(captionText,
                new Point(centre.X - captionText.Width / 2.0, y + valueText.Height + 2));
        }
    }

    public static readonly DependencyProperty CaptionBrushProperty =
        DependencyProperty.Register(nameof(CaptionBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush CaptionBrush {
        get => (Brush)GetValue(CaptionBrushProperty);
        set => SetValue(CaptionBrushProperty, value);
    }
}
