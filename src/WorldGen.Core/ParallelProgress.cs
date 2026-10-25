using System.Diagnostics;

namespace WorldGen;

/// <summary>
/// Lightweight, zero-lock progress tracker partitioned by hardware worker threads.
/// Workers report only at coarse natural breaks (e.g., every 8–16 rows or at iteration boundaries),
/// never inside inner pixel loops, and UI notifications are throttled to ~20 Hz so compute speed is unaffected.
/// </summary>
public sealed class ParallelProgress
{
    private readonly float[] _workerFractions;
    private readonly float[] _snapshotBuffer;
    private readonly Action<float[]>? _onProgress;
    private readonly long _minNotifyTicks;
    private long _lastNotifyTimestamp;
    private int _notifyGate;

    public ParallelProgress(int workerCount = 0, Action<float[]>? onProgress = null, int minNotifyIntervalMs = 45)
    {
        int count = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount);
        _workerFractions = new float[count];
        _snapshotBuffer = new float[count];
        _onProgress = onProgress;
        _minNotifyTicks = (long)(Stopwatch.Frequency * (Math.Max(10, minNotifyIntervalMs) / 1000.0));
        _lastNotifyTimestamp = 0;
    }

    public int WorkerCount => _workerFractions.Length;

    /// <summary>
    /// Resets all thread segments to 0 and notifies the listener.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_workerFractions, 0, _workerFractions.Length);
        ForceNotify();
    }

    /// <summary>
    /// Sets all thread segments to a uniform fraction (used at coarse sequential phase boundaries).
    /// </summary>
    public void SetAll(float fraction, bool forceNotify = false)
    {
        float clamped = Math.Clamp(fraction, 0f, 1f);
        Array.Fill(_workerFractions, clamped);
        if (forceNotify)
        {
            ForceNotify();
        }
        else
        {
            TryNotifyThrottled();
        }
    }

    /// <summary>
    /// Marks all thread segments as 100% complete and flushes a final notification.
    /// </summary>
    public void Complete() => SetAll(1f, forceNotify: true);

    /// <summary>
    /// Updates a single worker thread's completion fraction (in [0, 1]) and triggers a throttled notification.
    /// Call only at coarse outer-loop intervals, never per pixel.
    /// </summary>
    public void ReportWorker(int workerIndex, float fraction)
    {
        if ((uint)workerIndex < (uint)_workerFractions.Length)
        {
            _workerFractions[workerIndex] = Math.Clamp(fraction, 0f, 1f);
            TryNotifyThrottled();
        }
    }

    /// <summary>
    /// Partitions <c>[0, totalItems)</c> into <see cref="WorkerCount"/> contiguous slices and executes
    /// <paramref name="body"/> in parallel across workers, reporting each worker's progress every
    /// <paramref name="reportStride"/> items within `[minFraction, maxFraction]`.
    /// </summary>
    public static void ForRange(
        int totalItems,
        ParallelProgress? progress,
        Action<int> body,
        float minFraction = 0f,
        float maxFraction = 1f)
    {
        if (totalItems <= 0)
            return;

        if (progress is null)
        {
            Parallel.For(0, totalItems, body);
            return;
        }

        int workers = Math.Min(progress.WorkerCount, totalItems);
        float span = maxFraction - minFraction;

        Parallel.For(0, workers, workerId =>
        {
            int start = (int)((long)workerId * totalItems / workers);
            int end = (int)((long)(workerId + 1) * totalItems / workers);
            int count = end - start;
            if (count <= 0)
            {
                progress.ReportWorker(workerId, maxFraction);
                return;
            }

            // Report at most ~16 times per worker slice so overhead is negligible.
            int stride = Math.Max(4, count / 16);
            int nextReport = start + stride;

            for (int i = start; i < end; i++)
            {
                body(i);

                if (i + 1 >= nextReport || i + 1 == end)
                {
                    float localFrac = (float)(i + 1 - start) / count;
                    progress.ReportWorker(workerId, minFraction + span * localFrac);
                    nextReport += stride;
                }
            }
        });
    }

    private void TryNotifyThrottled()
    {
        if (_onProgress is null)
            return;

        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastNotifyTimestamp);
        if (now - last < _minNotifyTicks)
            return;

        if (Interlocked.CompareExchange(ref _notifyGate, 1, 0) != 0)
            return;

        try
        {
            Volatile.Write(ref _lastNotifyTimestamp, now);
            Array.Copy(_workerFractions, _snapshotBuffer, _workerFractions.Length);
            _onProgress(_snapshotBuffer);
        }
        finally
        {
            Volatile.Write(ref _notifyGate, 0);
        }
    }

    private void ForceNotify()
    {
        if (_onProgress is null)
            return;

        if (Interlocked.Exchange(ref _notifyGate, 1) == 0)
        {
            try
            {
                Volatile.Write(ref _lastNotifyTimestamp, Stopwatch.GetTimestamp());
                Array.Copy(_workerFractions, _snapshotBuffer, _workerFractions.Length);
                _onProgress(_snapshotBuffer);
            }
            finally
            {
                Volatile.Write(ref _notifyGate, 0);
            }
        }
    }
}
