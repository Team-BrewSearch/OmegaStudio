namespace Omega.Plugins;
public sealed class CachedValue<T> : ICacheSlot
    where T : class
{
    private readonly Func<T> _load;
    private readonly Func<T, long> _cost;
    private readonly Lock _gate = new();
    private T? _value;
    public CachedValue(Func<T> load, Func<T, long> cost)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(cost);
        _load = load;
        _cost = cost;
    }
    LinkedListNode<PayloadCache.Entry>? ICacheSlot.Kept { get; set; }
    public bool HasValue
    {
        get
        {
            lock (_gate)
            {
                return _value is not null;
            }
        }
    }
    public T Value
    {
        get
        {
            T? held;
            lock (_gate)
            {
                held = _value;
            }
            if (held is not null)
            {
                PayloadCache.Touch(this);
                return held;
            }
            T loaded = _load();
            Set(loaded);
            return loaded;
        }
    }
    public void Set(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _value = value;
        }
        if (!PayloadCache.Track(this, _cost(value)))
        {
            lock (_gate)
            {
                _value = null;
            }
        }
    }
    public void Clear()
    {
        PayloadCache.Release(this);
        lock (_gate)
        {
            _value = null;
        }
    }
    void ICacheSlot.Drop()
    {
        lock (_gate)
        {
            _value = null;
        }
    }
}
