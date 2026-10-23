namespace Omega.Plugins;
internal static class PayloadCache
{
    private static readonly Lock s_gate = new();
    private static readonly LinkedList<Entry> s_kept = new();
    private static long s_bytes;
    public static void Keep(StreamingAssetNode owner, byte[] payload)
    {
        lock (s_gate)
        {
            Forget(owner);
            if (payload.LongLength > MemoryBudget.PayloadCache)
            {
                return;
            }
            owner.Cached = payload;
            owner.Kept = s_kept.AddLast(new Entry(new WeakReference<StreamingAssetNode>(owner), payload.LongLength));
            s_bytes += payload.LongLength;
            while (s_bytes > MemoryBudget.PayloadCache)
            {
                Evict(s_kept.First!);
            }
        }
    }
    public static void Touch(StreamingAssetNode owner)
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
    public static void Release(StreamingAssetNode owner)
    {
        lock (s_gate)
        {
            Forget(owner);
        }
    }
    private static void Forget(StreamingAssetNode owner)
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
        if (kept.Value.Owner.TryGetTarget(out StreamingAssetNode? owner))
        {
            owner.Cached = null;
            owner.Kept = null;
        }
    }
    internal readonly record struct Entry(WeakReference<StreamingAssetNode> Owner, long Bytes);
}
