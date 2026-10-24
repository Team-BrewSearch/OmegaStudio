namespace Omega.Plugins;
internal interface ICacheSlot
{
    LinkedListNode<PayloadCache.Entry>? Kept { get; set; }
    void Drop();
}
internal static class PayloadCache
{
    private static readonly Lock s_gate = new();
    private static readonly LinkedList<Entry> s_kept = new();
    private static long s_bytes;
    public static long Bytes
    {
        get
        {
            lock (s_gate)
            {
                return s_bytes;
            }
        }
    }
    public static void Keep(StreamingAssetNode owner, byte[] payload)
    {
        lock (s_gate)
        {
            Forget(owner);
            if (payload.LongLength > MemoryBudget.PayloadCache)
            {
                return;
            }
            if (Track(owner, payload.LongLength))
            {
                owner.Cached = payload;
            }
        }
    }
    public static bool Track(ICacheSlot owner, long bytes)
    {
        lock (s_gate)
        {
            Forget(owner);
            if (bytes > MemoryBudget.PayloadCache)
            {
                return false;
            }
            owner.Kept = s_kept.AddLast(new Entry(new WeakReference<ICacheSlot>(owner), bytes));
            s_bytes += bytes;
            while (s_bytes > MemoryBudget.PayloadCache && s_kept.First is { } oldest && !ReferenceEquals(oldest, owner.Kept))
            {
                Evict(oldest);
            }
            return true;
        }
    }
    public static void Touch(ICacheSlot owner)
    {
        lock (s_gate)
        {
            if (owner.Kept is { Next: not null } kept)
            {
                s_kept.Remove(kept);
                s_kept.AddLast(kept);
            }
        }
    }
    public static void Release(ICacheSlot owner)
    {
        lock (s_gate)
        {
            Forget(owner);
        }
    }
    private static void Forget(ICacheSlot owner)
    {
        if (owner.Kept is { } kept)
        {
            Evict(kept);
        }
    }
    private static void Evict(LinkedListNode<Entry> kept)
    {
        s_kept.Remove(kept);
        s_bytes -= kept.Value.Bytes;
        if (kept.Value.Owner.TryGetTarget(out ICacheSlot? owner))
        {
            owner.Kept = null;
            owner.Drop();
        }
    }
    internal readonly record struct Entry(WeakReference<ICacheSlot> Owner, long Bytes);
}
