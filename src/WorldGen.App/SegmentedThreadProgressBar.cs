using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WorldGen.App;

/// <summary>
/// Single-line horizontal progress bar segmented into N equal cells (one per hardware worker thread).
/// Each thread's segment fills independently from left to right as that thread reports progress at coarse breaks.
/// </summary>
public sealed class SegmentedThreadProgressBar : Control
{
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#1B2231"));
    private static readonly IPen TrackBorderPen = new Pen(new SolidColorBrush(Color.Parse("#3A4560")), 1.0);
    private static readonly IBrush ActiveFillBrush = new SolidColorBrush(Color.Parse("#38BDF8"));

    private float[] _fractions = new float[Math.Max(1, Environment.ProcessorCount)];
    private int _uiUpdatePending;

    public int SegmentCount => _fractions.Length;

    protected override Size MeasureOverride(Size availableSize)
    {
        // A bare Control has no content, so give the bar an intrinsic height instead of collapsing to zero.
        double desiredHeight = double.IsInfinity(Height) ? 14.0 : Height;
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0.0 : availableSize.Width,
            desiredHeight);
    }

    /// <summary>
    /// Thread-safe, non-blocking update called from <see cref="WorldGen.ParallelProgress"/>.
    /// Coalesces UI invalidations so worker threads never block or flood the dispatcher.
    /// </summary>
    public void UpdateFractions(ReadOnlySpan<float> workerFractions)
    {
        if (_fractions.Length != workerFractions.Length && workerFractions.Length > 0)
        {
            _fractions = new float[workerFractions.Length];
        }

        for (int i = 0; i < _fractions.Length && i < workerFractions.Length; i++)
        {
            _fractions[i] = Math.Clamp(workerFractions[i], 0f, 1f);
        }

        if (Interlocked.CompareExchange(ref _uiUpdatePending, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _uiUpdatePending, 0);
                InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double totalW = Bounds.Width;
        double totalH = Bounds.Height;
        if (totalW <= 2 || totalH <= 2)
            return;

        int n = Math.Max(1, _fractions.Length);
        const double gap = 2.0;
        double totalGap = gap * (n - 1);
        double segW = Math.Max(2.0, (totalW - totalGap) / n);

        // Stay blue when the current stage reaches 100%. Completion of one stage is not completion of the
        // pipeline, and the next stage reuses this same bar. A green success colour here would claim the
        // whole run is finished while later stages are still available.
        IBrush fillBrush = ActiveFillBrush;

        for (int i = 0; i < n; i++)
        {
            double x = i * (segW + gap);
            var segRect = new Rect(x, 0, segW, totalH);
            context.DrawRectangle(TrackBrush, TrackBorderPen, segRect, 2.0, 2.0);

            float frac = Math.Clamp(_fractions[i], 0f, 1f);
            if (frac > 0.005f)
            {
                double fillW = Math.Max(1.0, (segW - 2.0) * frac);
                var fillRect = new Rect(x + 1.0, 1.0, fillW, Math.Max(1.0, totalH - 2.0));
                context.DrawRectangle(fillBrush, null, fillRect, 1.5, 1.5);
            }
        }
    }
}
