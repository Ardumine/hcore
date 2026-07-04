using System.Diagnostics;
using System.Threading.Channels;
using HCore.Modules.Base;
using static HCore.Modules.Base.DisconnectReason;

namespace HCore.Main.Internal;

/// <summary>
/// One subscription to a facet. Owns its per-subscriber bounded
/// <see cref="Channel{T}"/> (the isolation unit — a slow/throwing consumer
/// cannot stall the producer or other subscribers) and a single consumer task
/// on the thread pool (the execution unit — no thread explosion). Implements
/// the circuit breaker: trips on sustained overload, handler failure, or
/// producer death, funnelling into the typed <see cref="DisconnectReason"/>.
/// </summary>
internal sealed class DataSubscription<T> : ISubscription where T : class
{
    private readonly Facet<T> _facet;
    private readonly Func<DataEvent<T>, CancellationToken, ValueTask> _handler;
    private readonly Action<DisconnectReason>? _onDisconnected;
    private readonly FacetKind _kind;
    private readonly DispatchPolicy _policy;
    private readonly int _bound;
    private readonly IModuleLogger _logger;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<DataEvent<T>>? _channel;
    private readonly SemaphoreSlim? _parallelGate;
    private Task _consumerTask = Task.CompletedTask;

    private int _state = (int)SubscriptionState.Active;
    private DisconnectReason? _disconnectReason;

    private long _consumerSkippedCount;
    private int _pending;                 // approximate backlog (producer-accounted)
    private long _overflowStartTicks;     // 0 = not in sustained overflow
    private long _throwWindowStartTicks;  // 0 = not in a throw streak

    // Breaker thresholds (DATA_PLANE_DESIGN.md Part VII).
    private const long OverloadWindowMs = 2000;
    private const long ThrowWindowMs = 2000;

    public DataSubscription(
        Facet<T> facet,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected,
        FacetKind kind,
        DispatchPolicy policy,
        int bound,
        IModuleLogger logger)
    {
        _facet = facet;
        _handler = handler;
        _onDisconnected = onDisconnected;
        _kind = kind;
        _policy = policy;
        _bound = bound;
        _logger = logger;

        if (_policy != DispatchPolicy.WaitForAll)
        {
            _channel = Channel.CreateBounded<DataEvent<T>>(new BoundedChannelOptions(bound)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        if (_policy == DispatchPolicy.ParallelUnordered)
        {
            var dop = Math.Max(1, Environment.ProcessorCount);
            _parallelGate = new SemaphoreSlim(dop, dop);
        }
    }

    public SubscriptionState State => (SubscriptionState)Volatile.Read(ref _state);
    public DisconnectReason? DisconnectReason => _disconnectReason;
    public long ConsumerSkippedCount => Interlocked.Read(ref _consumerSkippedCount);

    /// <summary>Launch the consumer loop after the facet has published this subscription.</summary>
    public void Start()
    {
        if (_channel is null)
        {
            return; // WaitForAll: synchronous dispatch, no consumer task
        }

        _consumerTask = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Called by the producer's <c>Publish</c> on the producer's thread. For
    /// channel-based policies, writes (drop-oldest on overflow) and tracks the
    /// backlog for the overload breaker. For <see cref="DispatchPolicy.WaitForAll"/>,
    /// invokes the handler synchronously and blocks until it finishes.
    /// </summary>
    public void Dispatch(DataEvent<T> evt)
    {
        if (Volatile.Read(ref _state) != (int)SubscriptionState.Active)
        {
            return;
        }

        if (_channel is null) // WaitForAll — blocking backpressure
        {
            try
            {
                _handler(evt, _cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consumerSkippedCount);
                _logger.W($"handler threw on seq {evt.Sequence} (WaitForAll): {ex.Message}");
                Trip(HandlerException);
            }

            return;
        }

        var pending = Interlocked.Increment(ref _pending);
        if (pending > _bound)
        {
            // DropOldest will drop one and add one — net depth unchanged.
            Interlocked.Decrement(ref _pending);

            if (_kind == FacetKind.Stream)
            {
                var now = Stopwatch.GetTimestamp();
                Interlocked.CompareExchange(ref _overflowStartTicks, now, 0);
                var start = Interlocked.Read(ref _overflowStartTicks);
                if (start != 0 && MsBetween(start, now) >= OverloadWindowMs)
                {
                    _logger.W($"overload: facet '{_facet.FacetName}' queue sustained at {_bound} for >{OverloadWindowMs}ms; disconnecting subscriber.");
                    Trip(Overload);
                    return;
                }
            }

            // Cell: coalesce silently (dropping intermediates is the design).
        }
        else if (pending <= _bound / 2)
        {
            // Hysteresis: only reset the sustained-overload window once the queue
            // has drained WELL below capacity. A momentary single-frame dip (a slow
            // consumer draining one item between two fast publishes) must not reset
            // the window — that would let a genuinely overloaded stream never trip.
            Interlocked.Exchange(ref _overflowStartTicks, 0);
        }

        _channel.Writer.TryWrite(evt);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var evt in _channel!.Reader.ReadAllAsync(_cts.Token))
            {
                if (Volatile.Read(ref _state) != (int)SubscriptionState.Active)
                {
                    break;
                }

                if (_policy == DispatchPolicy.ParallelUnordered)
                {
                    try
                    {
                        await _parallelGate!.WaitAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    _ = DispatchParallelAsync(evt);
                }
                else
                {
                    try
                    {
                        await _handler(evt, _cts.Token);
                        Interlocked.Exchange(ref _throwWindowStartTicks, 0);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consumerSkippedCount);
                        _logger.W($"handler threw on seq {evt.Sequence}: {ex.Message}");
                        HandleThrow();
                        if (Volatile.Read(ref _state) != (int)SubscriptionState.Active)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (Exception ex)
        {
            _logger.E($"consumer loop crashed on facet '{_facet.FacetName}': {ex.Message}");
        }
    }

    private async Task DispatchParallelAsync(DataEvent<T> evt)
    {
        try
        {
            try
            {
                await _handler(evt, _cts.Token);
                Interlocked.Exchange(ref _throwWindowStartTicks, 0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consumerSkippedCount);
                _logger.W($"handler threw on seq {evt.Sequence}: {ex.Message}");
                HandleThrow();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
            _parallelGate!.Release();
        }
    }

    /// <summary>Handler-exception policy (per-facet): cell = one-strike; stream = trip on sustained throws.</summary>
    private void HandleThrow()
    {
        if (_kind == FacetKind.Cell)
        {
            Trip(HandlerException);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _throwWindowStartTicks, now, 0);
        var start = Interlocked.Read(ref _throwWindowStartTicks);
        if (start != 0 && MsBetween(start, now) >= ThrowWindowMs)
        {
            _logger.W($"sustained handler exceptions on facet '{_facet.FacetName}' for >{ThrowWindowMs}ms; disconnecting subscriber.");
            Trip(HandlerException);
        }
    }

    /// <summary>
    /// Transition Active → Tripped, cancel the consumer, drop the queue, detach
    /// from the facet, then fire the optional callback (outside any facet lock).
    /// Idempotent: a tripped/disposed subscription is already dead.
    /// </summary>
    public void Trip(DisconnectReason reason)
    {
        if (Interlocked.CompareExchange(ref _state, (int)SubscriptionState.Tripped, (int)SubscriptionState.Active) != (int)SubscriptionState.Active)
        {
            return;
        }

        _disconnectReason = reason;
        _cts.Cancel();
        _channel?.Writer.TryComplete();
        _facet.RemoveSubscriber(this);

        try
        {
            _onDisconnected?.Invoke(reason);
        }
        catch (Exception ex)
        {
            _logger.E($"onDisconnected callback threw on facet '{_facet.FacetName}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, (int)SubscriptionState.Disposed, (int)SubscriptionState.Active) != (int)SubscriptionState.Active)
        {
            return;
        }

        _disconnectReason = Disposed;
        _cts.Cancel();
        _channel?.Writer.TryComplete();
        _facet.RemoveSubscriber(this);
    }

    private static long MsBetween(long startTicks, long endTicks)
        => (endTicks - startTicks) * 1000L / Stopwatch.Frequency;
}
