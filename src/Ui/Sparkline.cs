using System.Windows;
using System.Windows.Media;

namespace CpuGlassMonitor.Ui;

/// <summary>迷你折线图：显示指标最近一段时间的走势。</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinValueProperty = DependencyProperty.Register(
        nameof(MinValue), typeof(double?), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double?), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0x6F, 0xD3, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowFillProperty = DependencyProperty.Register(
        nameof(ShowFill), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double? MinValue
    {
        get => (double?)GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, value);
    }

    public double? MaxValue
    {
        get => (double?)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool ShowFill
    {
        get => (bool)GetValue(ShowFillProperty);
        set => SetValue(ShowFillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        IReadOnlyList<double>? values = Values;
        if (values is null || values.Count == 0) return;

        double pad = 1.2;
        double plotW = w - pad * 2;
        double plotH = h - pad * 2;
        if (plotW <= 0 || plotH <= 0) return;

        double min, max;
        if (MinValue.HasValue && MaxValue.HasValue)
        {
            min = MinValue.Value;
            max = MaxValue.Value;
        }
        else
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in values)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            if (lo == double.MaxValue) return;

            if (MinValue.HasValue) lo = MinValue.Value;
            if (MaxValue.HasValue) hi = MaxValue.Value;

            double span = hi - lo;
            if (span < 1e-6)
            {
                span = Math.Max(Math.Abs(hi) * 0.08, 1.0);
                lo -= span / 2;
                hi += span / 2;
            }
            else
            {
                double padY = span * 0.12;
                lo -= padY;
                hi += padY;
            }

            min = lo;
            max = hi;
        }

        double range = max - min;
        if (range < 1e-6) return;

        int n = values.Count;
        var points = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = pad + (n == 1 ? plotW / 2 : plotW * i / (n - 1.0));
            double norm = Math.Clamp((values[i] - min) / range, 0, 1);
            double y = pad + plotH * (1 - norm);
            points[i] = new Point(x, y);
        }

        var strokeBrush = new SolidColorBrush(Accent) { Opacity = 0.92 };
        strokeBrush.Freeze();
        var pen = new Pen(strokeBrush, 1.25)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();

        if (ShowFill && n > 1)
        {
            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, h - pad * 0.4), true, true);
                ctx.LineTo(points[0], false, false);
                for (int i = 1; i < n; i++) ctx.LineTo(points[i], false, false);
                ctx.LineTo(new Point(points[n - 1].X, h - pad * 0.4), false, false);
            }

            geo.Freeze();
            var fill = new LinearGradientBrush(
                Color.FromArgb(64, Accent.R, Accent.G, Accent.B),
                Color.FromArgb(0, Accent.R, Accent.G, Accent.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, geo);
        }

        if (n == 1)
        {
            dc.DrawEllipse(strokeBrush, null, points[0], 1.4, 1.4);
            return;
        }

        var lineGeo = new StreamGeometry();
        using (StreamGeometryContext ctx = lineGeo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < n; i++) ctx.LineTo(points[i], false, false);
        }

        lineGeo.Freeze();
        dc.DrawGeometry(null, pen, lineGeo);
    }
}
