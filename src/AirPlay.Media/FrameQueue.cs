using System.Diagnostics;

namespace AirPlay.Media;

/// <summary>A decoded picture waiting to be shown at its presentation time.</summary>
public sealed class QueuedFrame
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Presentation time on the sender's clock (ns), or null to show at once.</summary>
    public long? RemoteNs { get; init; }
    public long EnqueuedTicks { get; set; }
}

/// <summary>
/// Holds decoded pictures until their presentation time (video waiting for audio).
/// The decode thread enqueues; the display picks the newest picture that is due.
/// Pixel buffers are pooled to avoid allocating a full frame for every picture.
/// </summary>
public sealed class FrameQueue
{
    /// <summary>A picture is shown anyway after waiting this long (protects against bad timestamps).</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(1);
    public const int MaxQueued = 45;

    private readonly object _lock = new();
    private readonly Queue<QueuedFrame> _queue = new();
    private readonly Stack<byte[]> _pool = new();
    private long _version;
    private long? _pausedSinceTicks;

    public long Dropped { get; private set; }
    public long ForcedByTimeout { get; private set; }
    public long Version { get { lock (_lock) return _version; } }

    public void Enqueue(BgraFrame f, long? remoteNs)
    {
        lock (_lock)
        {
            var size = f.Stride * f.Height;
            var buffer = _pool.Count > 0 && _pool.Peek().Length == size ? _pool.Pop() : new byte[size];
            Buffer.BlockCopy(f.Pixels, 0, buffer, 0, size);
            _queue.Enqueue(new QueuedFrame
            {
                Pixels = buffer, Width = f.Width, Height = f.Height,
                RemoteNs = remoteNs, EnqueuedTicks = Stopwatch.GetTimestamp(),
            });
            while (_queue.Count > MaxQueued)
            {
                Recycle(_queue.Dequeue().Pixels);
                Dropped++;
            }
            _version++;
        }
    }

    /// <summary>
    /// Returns the newest picture whose time has come (older due pictures are skipped),
    /// or null if none is due yet. <paramref name="nowRemoteNs"/> null = everything is due.
    /// Give the returned picture's buffer back with <see cref="Recycle"/> when done.
    /// </summary>
    /// <param name="paused">Playback is paused: pictures wait as long as needed (no 1 s safety limit).</param>
    public QueuedFrame? TakeDue(long? nowRemoteNs, bool paused = false)
    {
        lock (_lock)
        {
            QueuedFrame? due = null;
            var now = Stopwatch.GetTimestamp();
            if (paused) _pausedSinceTicks ??= now;
            else if (_pausedSinceTicks is { } since)
            {
                // Don't count the paused time against the pictures' waiting limit.
                var pausedTicks = now - since;
                foreach (var f in _queue) f.EnqueuedTicks += pausedTicks;
                _pausedSinceTicks = null;
            }
            while (_queue.Count > 0)
            {
                var head = _queue.Peek();
                var heldTooLong = !paused && Stopwatch.GetElapsedTime(head.EnqueuedTicks, now) > MaxHold;
                var isDue = nowRemoteNs is null || head.RemoteNs is null || head.RemoteNs <= nowRemoteNs || heldTooLong;
                if (!isDue) break;
                if (heldTooLong && nowRemoteNs is not null && head.RemoteNs > nowRemoteNs) ForcedByTimeout++;
                if (due is not null) { Recycle(due.Pixels); Dropped++; }
                due = _queue.Dequeue();
            }
            return due;
        }
    }

    public int Count { get { lock (_lock) return _queue.Count; } }

    public void Recycle(byte[] pixels)
    {
        lock (_lock)
        {
            if (_pool.Count < 8) _pool.Push(pixels);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            while (_queue.Count > 0) Recycle(_queue.Dequeue().Pixels);
        }
    }
}
