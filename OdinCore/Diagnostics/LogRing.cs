// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// SPDX-License-Identifier: GPL-3.0-or-later

namespace OdinCore.Diagnostics;

/// <summary>
/// A bounded store of every log event, oldest dropped first.
/// </summary>
public sealed class LogRing
{
    private readonly OdinEvent[] _slots;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public LogRing(int capacity = 20_000)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");

        _slots = new OdinEvent[capacity];
    }

    /// <summary>How many events the ring can hold before it begins to overwrite.</summary>
    public int Capacity => _slots.Length;

    /// <summary>How many events the ring currently holds.</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>
    /// Total events ever appended, including ones that have since been
    /// overwritten. The UI uses the difference against <see cref="Count"/>
    /// to say plainly that older lines were dropped.
    /// </summary>
    public long TotalAppended { get; private set; }

    /// <summary>Raised after an event has been stored. Handlers must not throw.</summary>
    public event Action<OdinEvent>? Added;

    public void Add(OdinEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        lock (_gate)
        {
            _slots[_next] = e;
            _next = (_next + 1) % _slots.Length;

            if (_count < _slots.Length)
                _count++;

            TotalAppended++;
        }

        Added?.Invoke(e);
    }

    /// <summary>Every retained event, oldest first.</summary>
    public IReadOnlyList<OdinEvent> Snapshot()
    {
        lock (_gate)
        {
            var result = new OdinEvent[_count];
            int start = (_next - _count + _slots.Length) % _slots.Length;

            for (int i = 0; i < _count; i++)
                result[i] = _slots[(start + i) % _slots.Length];

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_slots);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> face on the ring, so every OdinCore
    /// operation can take a plain IProgress and the UI never has to adapt.
    /// </summary>
    public IProgress<OdinEvent> AsProgress() => new RingProgress(this);

    private sealed class RingProgress : IProgress<OdinEvent>
    {
        private readonly LogRing _ring;
        internal RingProgress(LogRing ring) => _ring = ring;
        public void Report(OdinEvent value) => _ring.Add(value);
    }
}

/// <summary>
/// Progress for one partition's upload.
/// </summary>
public sealed record TransferProgress(string PartitionName, long BytesSentForFile,
                                      long FileLength, int PercentForFile);

/// <summary>
/// Progress for a whole flash run.
/// </summary>
public sealed record FlashProgress(string? PartitionName, int PartitionPercent,
                                   long OverallBytesDone, long OverallBytesTotal,
                                   int OverallPercent, bool Indeterminate, string Phase);

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback on the reporting
/// thread, synchronously and in order.
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SyncProgress(Action<T> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
