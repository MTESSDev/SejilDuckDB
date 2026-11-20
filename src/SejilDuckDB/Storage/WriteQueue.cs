using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace SejilDuckDB.Storage;

/// <summary>
/// The ONE queue through which everything is written to the log files of a folder in this process: the batches received on
/// <c>/log</c> and the events of a local Serilog sink alike. A single writer takes what has gathered (a few milliseconds' worth, up to
/// <see cref="MaxBatchEvents"/>) and appends it to each day's file in one go, so a hundred requests at once cost a few appends, not a
/// hundred.
/// <list type="bullet">
/// <item><see cref="SubmitAsync"/> (the route <c>/log</c>) returns when the events are <b>written</b>: true means they are on disk, false
/// means they are not (the queue is full, or the write failed) and the sender must send them again, which is what Serilog.Sinks.Http does
/// when it is answered 503. Nothing is kept for it.</item>
/// <item><see cref="TryEnqueue"/> (the local sink) returns at once, never blocks and never throws; the events are kept and written again
/// until they are written, and when the queue is full the newest are dropped (and counted).</item>
/// </list>
/// The queue holds at most <c>limit</c> events that are not written yet, whoever put them there.
/// </summary>
internal sealed class WriteQueue
{
    /// <summary>Most events written in one go.</summary>
    public const int MaxBatchEvents = 5000;

    public const int DefaultLimit = 50_000;

    private static readonly ConcurrentDictionary<string, WriteQueue> Queues = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Item(IReadOnlyList<LogEvent> Events, string App, TaskCompletionSource<bool>? Done);

    private readonly Func<IReadOnlyList<(LogEvent Event, string App)>, Task<bool>> _write;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeSpan _retryDelay;
    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Item> _retry = new();          // only the worker touches it
    private DateTime _retryAt;                            // only the worker touches it
    private int _queued;                                  // events accepted and not written yet
    private int _flushing;
    private long _dropped;

    /// <param name="write">Appends the events to the files. True: written. False or an exception: not written.</param>
    /// <param name="limit">Most events waiting at once.</param>
    /// <param name="window">How long the writer lets a burst gather before it writes. A request waits this long at most.</param>
    /// <param name="retryDelay">How long events of the local sink wait before the next try, after a failed write.</param>
    public WriteQueue(Func<IReadOnlyList<(LogEvent Event, string App)>, Task<bool>> write, int limit, TimeSpan window, TimeSpan retryDelay)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _limit = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
        _window = window;
        _retryDelay = retryDelay;
        _ = Task.Run(RunAsync);
    }

    /// <summary>The queue of a folder: every store on the folder shares it, so the route and the sink really do share one.</summary>
    public static WriteQueue For(string directory, Func<WriteQueue> create)
        => Queues.GetOrAdd(Path.GetFullPath(directory), _ => create());

    /// <summary>Stops the queue of a folder. For tests, and for a host that is going away.</summary>
    public static void Forget(string directory)
    {
        if (Queues.TryRemove(Path.GetFullPath(directory), out var queue))
        {
            queue._stop.Cancel();
        }
    }

    /// <summary>Events accepted and not written yet.</summary>
    public int Pending => Volatile.Read(ref _queued);

    /// <summary>Puts the events in the queue and returns when they are written. False: they are not, and the caller must try again.</summary>
    public Task<bool> SubmitAsync(IReadOnlyList<LogEvent> events, string app)
    {
        if (events.Count == 0)
        {
            return Task.FromResult(true);
        }

        if (!Reserve(events.Count))
        {
            return Task.FromResult(false); // full: the sender backs off, which is what its 503 is for
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Writer.TryWrite(new Item(events, app, done));
        return done.Task;
    }

    /// <summary>Puts the events in the queue and returns at once. False: the queue was full and they were dropped.</summary>
    public bool TryEnqueue(IReadOnlyList<LogEvent> events, string app)
    {
        if (events.Count == 0)
        {
            return true;
        }

        if (!Reserve(events.Count))
        {
            Interlocked.Add(ref _dropped, events.Count);
            return false;
        }

        _channel.Writer.TryWrite(new Item(events, app, null));
        return true;
    }

    /// <summary>The events dropped since the last call, because the queue was full.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    /// <summary>
    /// Waits until everything that was put in the queue is written, trying again at once (not after the usual delay) what failed.
    /// False: the time ran out first.
    /// </summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        Interlocked.Increment(ref _flushing);
        try
        {
            var end = DateTime.UtcNow + timeout;
            while (Pending > 0)
            {
                if (DateTime.UtcNow >= end)
                {
                    return false;
                }

                await Task.Delay(20);
            }

            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _flushing);
        }
    }

    // A single over-large batch is accepted when the queue is empty: otherwise it could never be written.
    private bool Reserve(int count)
    {
        while (true)
        {
            var queued = Volatile.Read(ref _queued);
            if (queued > 0 && queued + count > _limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _queued, queued + count, queued) == queued)
            {
                return true;
            }
        }
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var items = new List<Item>();

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Wait for something to write: a new item, or a failed one whose turn has come (looked at often, so that a flush
                // does not wait for the usual delay).
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    if (_retry.Count > 0)
                    {
                        wait.CancelAfter(TimeSpan.FromMilliseconds(50));
                    }

                    try
                    {
                        if (!await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        // nothing new: perhaps a retry is due
                    }
                }

                var retryDue = _retry.Count > 0 && (Volatile.Read(ref _flushing) > 0 || DateTime.UtcNow >= _retryAt);
                if (!reader.TryPeek(out _) && !retryDue)
                {
                    continue;
                }

                if (_window > TimeSpan.Zero && reader.TryPeek(out _))
                {
                    await Task.Delay(_window, _stop.Token).ConfigureAwait(false); // lets a burst gather: one append for all of it
                }

                items.Clear();
                var count = 0;
                if (retryDue)
                {
                    items.AddRange(_retry);
                    _retry.Clear();
                    count = items.Sum(i => i.Events.Count);
                }

                while (count < MaxBatchEvents && reader.TryRead(out var item))
                {
                    items.Add(item);
                    count += item.Events.Count;
                }

                if (items.Count == 0)
                {
                    continue;
                }

                var written = await WriteAsync(items).ConfigureAwait(false);

                foreach (var item in items)
                {
                    if (written || item.Done is not null)
                    {
                        Interlocked.Add(ref _queued, -item.Events.Count);
                        item.Done?.TrySetResult(written);   // the route is told the truth, and keeps nothing
                    }
                    else
                    {
                        _retry.Add(item);                    // the local sink's events are kept until they are written
                    }
                }

                if (!written && _retry.Count > 0)
                {
                    _retryAt = DateTime.UtcNow + _retryDelay;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private async Task<bool> WriteAsync(List<Item> items)
    {
        try
        {
            var events = new List<(LogEvent Event, string App)>(items.Sum(i => i.Events.Count));
            foreach (var item in items)
            {
                foreach (var e in item.Events)
                {
                    events.Add((e, item.App));
                }
            }

            return await _write(events).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>When the site stops, writes what is still waiting.</summary>
internal sealed class WriteQueueFlushService : IHostedService
{
    private readonly LogFileStore _store;

    public WriteQueueFlushService(LogFileStore store) => _store = store;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _store.Queue.FlushAsync(TimeSpan.FromSeconds(10));
}
