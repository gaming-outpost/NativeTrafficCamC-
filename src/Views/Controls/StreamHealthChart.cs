using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;

namespace CoastalCommandCenter.Views.Controls;

/// <summary>
/// Lightweight time-series chart for the three health metrics. Renders directly
/// via <see cref="Render"/> so we don't pull in a charting dependency. Each
/// series is normalised against its own min/max so all three fit on a single
/// shared plot area.
/// </summary>
public sealed class StreamHealthChart : Control
{
    public static readonly StyledProperty<CameraHealthState?> StateProperty =
        AvaloniaProperty.Register<StreamHealthChart, CameraHealthState?>(nameof(State));

    public CameraHealthState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static readonly IBrush LatencyBrush = new SolidColorBrush(Color.FromRgb(0x4f, 0xc3, 0xf7));
    private static readonly IBrush LossBrush = new SolidColorBrush(Color.FromRgb(0xff, 0xb1, 0x4a));
    private static readonly IBrush BitrateBrush = new SolidColorBrush(Color.FromRgb(0x9c, 0xcc, 0x65));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), 1);

    private CameraHealthState? _subscribed;

    static StreamHealthChart()
    {
        AffectsRender<StreamHealthChart>(StateProperty);
        StateProperty.Changed.AddClassHandler<StreamHealthChart>((c, e) => c.OnStatePropertyChanged(e));
    }

    public StreamHealthChart()
    {
        MinHeight = 140;
        ClipToBounds = true;
    }

    private void OnStatePropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnStateChanged;

        _subscribed = e.NewValue as CameraHealthState;

        if (_subscribed is not null)
            _subscribed.PropertyChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraHealthState.SampleVersion))
        {
            if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
            else Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var bg = ThemeService.GetBrush("BrushPopoutWindowBackground");
        context.FillRectangle(bg, new Rect(0, 0, width, height));

        const double padLeft = 8;
        const double padRight = 8;
        const double padTop = 8;
        const double padBottom = 18;
        var plotW = Math.Max(0, width - padLeft - padRight);
        var plotH = Math.Max(0, height - padTop - padBottom);
        if (plotW <= 0 || plotH <= 0) return;

        // Gridlines (4 horizontal divisions).
        for (int i = 0; i <= 4; i++)
        {
            double y = padTop + plotH * i / 4.0;
            context.DrawLine(GridPen, new Point(padLeft, y), new Point(padLeft + plotW, y));
        }

        var samples = State?.SnapshotSamples();
        if (samples is null || samples.Count < 2)
        {
            DrawHint(context, "Awaiting samples...", padLeft + plotW / 2, padTop + plotH / 2);
            DrawLegend(context, width, padTop);
            return;
        }

        DrawSeries(context, samples, padLeft, padTop, plotW, plotH, s => s.LatencyMs, LatencyBrush);
        DrawSeries(context, samples, padLeft, padTop, plotW, plotH, s => s.LossPercent, LossBrush);
        DrawSeries(context, samples, padLeft, padTop, plotW, plotH, s => s.BitrateKbps, BitrateBrush);

        // X-axis labels: newest on the right.
        var xAxisLabel = $"{samples.Count * HealthThresholds.SampleIntervalSeconds}s ago → now";
        DrawAxisText(context, xAxisLabel, padLeft, padTop + plotH + 4);

        DrawLegend(context, width, padTop);
    }

    private static void DrawSeries(
        DrawingContext context,
        IReadOnlyList<HealthSample> samples,
        double x0, double y0, double plotW, double plotH,
        Func<HealthSample, double> selector,
        IBrush brush)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < samples.Count; i++)
        {
            var v = selector(samples[i]);
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (max - min < 0.0001) max = min + 1;

        var pen = new Pen(brush, 1.5);
        Point? prev = null;
        for (int i = 0; i < samples.Count; i++)
        {
            double tx = x0 + plotW * i / Math.Max(1, samples.Count - 1);
            double v = selector(samples[i]);
            double normalised = (v - min) / (max - min);
            double ty = y0 + plotH * (1 - normalised);
            var p = new Point(tx, ty);
            if (prev is not null)
                context.DrawLine(pen, prev.Value, p);
            prev = p;
        }
    }

    private static void DrawLegend(DrawingContext context, double width, double padTop)
    {
        var entries = new (string Label, IBrush Brush)[]
        {
            ("Latency", LatencyBrush),
            ("Loss", LossBrush),
            ("Bitrate", BitrateBrush),
        };

        double x = width - 8;
        foreach (var entry in entries.Reverse())
        {
            var ft = new FormattedText(
                entry.Label,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                10,
                ThemeService.GetBrush("BrushTextWhite"));
            x -= ft.Width;
            context.DrawText(ft, new Point(x, padTop));
            x -= 6;
            context.FillRectangle(entry.Brush, new Rect(x - 8, padTop + 3, 8, 8));
            x -= 12;
        }
    }

    private static void DrawAxisText(DrawingContext context, string text, double x, double y)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            9,
            ThemeService.GetBrush("BrushTextSubtle"));
        context.DrawText(ft, new Point(x, y));
    }

    private static void DrawHint(DrawingContext context, string text, double cx, double cy)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            ThemeService.GetBrush("BrushTextSubtle"));
        context.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }
}
