using System.Diagnostics;
using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// One exposed data facet of an instance: <c>/proc/&lt;instance&gt;/&lt;facetName&gt;</c>.
/// Holds the per-facet sequence counter, the latest value (for
/// <see cref="IDataHost.ReadData{T}"/> / <c>cat</c>), and the subscriber list.
/// Implements <see cref="IExposedData{T}"/> so the producer's
/// <see cref="IExposedData{T}.Publish"/> fans out directly here.
/// </summary>
internal sealed class Facet<T> : IInternalFacet, IExposedData<T> where T : class
{
    private readonly string _instanceName;
    private readonly string _facetName;
    private readonly FacetKind _kind;
    private readonly DispatchPolicy _policy;
    private readonly int _bound;
    private readonly Func<T, string> _formatter;
    private readonly IModuleLogger _logger;

    private long _sequence;
    private long _lastPublishTicks;
    private bool _hasPrevPublish;
    private bool _hasValue;
    private T? _currentValue;

    private readonly List<DataSubscription<T>> _subscribers = new();
    private readonly object _subLock = new();
    private bool _dead;

    public Facet(string instanceName, string facetName, FacetKind kind, DispatchPolicy policy, int bound, Func<T, string>? formatter, IModuleLogger logger)
    {
        _instanceName = instanceName;
        _facetName = facetName;
        _kind = kind;
        _policy = policy == DispatchPolicy.Default
            ? (kind == FacetKind.Cell ? DispatchPolicy.Coalesce : DispatchPolicy.OrderedQueue)
            : policy;
        _bound = bound <= 0
            ? (kind == FacetKind.Cell ? 1 : 64)
            : bound;
        _formatter = formatter ?? (v => v?.ToString() ?? "");
        _logger = logger;
    }

    public string InstanceName => _instanceName;
    public string FacetName => _facetName;
    public FacetKind Kind => _kind;
    public Type ValueType => typeof(T);

    // --- IExposedData<T> ---

    public void Publish(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        long seq;
        long? delta;

        lock (_subLock)
        {
            if (_dead)
            {
                return;
            }

            seq = ++_sequence;
            var now = Stopwatch.GetTimestamp();
            delta = _hasPrevPublish ? now - _lastPublishTicks : null;
            _lastPublishTicks = now;
            _hasPrevPublish = true;

            _hasValue = true;
            _currentValue = value;
        }

        var evt = new DataEvent<T> { Data = value, Sequence = seq, InterFrameDelta = delta };

        List<DataSubscription<T>> snapshot;
        lock (_subLock)
        {
            snapshot = _subscribers.ToList();
        }

        foreach (var sub in snapshot)
        {
            sub.Dispatch(evt);
        }
    }

    public void Set(T value) => Publish(value);

    // --- reads ---

    public T? ReadCurrent()
    {
        lock (_subLock)
        {
            return _hasValue ? _currentValue : null;
        }
    }

    public string? FormatForCat()
    {
        bool has;
        T? val;
        lock (_subLock)
        {
            has = _hasValue;
            val = _currentValue;
        }

        return has ? _formatter(val!) : null;
    }

    // --- subscriptions ---

    public ISubscription Subscribe(Func<DataEvent<T>, CancellationToken, ValueTask> handler, Action<DisconnectReason>? onDisconnected)
    {
        var sub = new DataSubscription<T>(this, handler, onDisconnected, _kind, _policy, _bound, _logger);
        lock (_subLock)
        {
            if (_dead)
            {
                throw new InvalidOperationException(
                    $"Producer '{_instanceName}' has been killed; cannot subscribe to facet '{_facetName}'.");
            }

            _subscribers.Add(sub);
        }

        sub.Start();
        return sub;
    }

    public ISubscription SubscribeRaw(
        Func<object, long, long?, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected)
        => Subscribe((evt, ct) => handler(evt.Data, evt.Sequence, evt.InterFrameDelta, ct), onDisconnected);

    public void RemoveSubscriber(DataSubscription<T> sub)
    {
        lock (_subLock)
        {
            _subscribers.Remove(sub);
        }
    }

    // --- producer death ---

    public void NotifyProducerKilled()
    {
        List<DataSubscription<T>> snapshot;
        lock (_subLock)
        {
            _dead = true;
            snapshot = _subscribers.ToList();
            _subscribers.Clear();
        }

        foreach (var sub in snapshot)
        {
            sub.Trip(DisconnectReason.ProducerKilled);
        }
    }
}
