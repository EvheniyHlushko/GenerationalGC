namespace GenerationalGC.Runtime.Core;

/// <summary>
///     Non-moving arena: allocations are bump-only; freeing the region discards all objects at once.
///     Disallow inbound references from GC-managed segments into a Region (to avoid dangling after dispose).
/// </summary>
public sealed class Region : IDisposable
{
    internal readonly HashSet<long> ExternalGcRoots = [];
    internal readonly Segment Segment;

    internal Region(Segment segment)
    {
        Segment = segment;
    }

    public void Dispose()
    {
        RegionDisposed?.Invoke(this, EventArgs.Empty);
    }

    internal void AddExternalRoot(long abs)
    {
        if (abs != 0) ExternalGcRoots.Add(abs);
    }

    internal event EventHandler? RegionDisposed;
}