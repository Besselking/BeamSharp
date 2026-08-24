using System.Collections.ObjectModel;

namespace BeamSharp.Serialization;

/// <summary>
/// A list that refuses to change once the options holding it are frozen.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ErlSerializerOptions"/> refuses its scalar setters after <c>MakeReadOnly</c> because a
/// converter resolved under one configuration and then cached must not be asked to honour another.
/// A plain <see cref="List{T}"/> behind a read-only property gives that away: the reference cannot
/// be replaced, but everything it holds can.
/// </para>
/// <para>
/// Wrapping the list in a <see cref="ReadOnlyCollection{T}"/> at that point would not do, because
/// the freeze happens on first use rather than when the caller says so. Anyone holding the
/// collection from before keeps the mutable original, and the wrapper is a view over it, so the
/// additions show through anyway.
/// </para>
/// <para>
/// Deriving from <see cref="Collection{T}"/> means every mutation arrives at one of the four methods
/// below, including any the interface grows later.
/// </para>
/// </remarks>
internal sealed class FreezableList<T> : Collection<T>, IList<T>
{
    private bool _frozen;

    public FreezableList() { }

    public FreezableList(IEnumerable<T> items)
    {
        foreach (var item in items) Add(item);
    }

    public void Freeze() => _frozen = true;

    /// <summary>
    /// Re-implemented because <see cref="Collection{T}"/> answers this false unconditionally, and a
    /// collection that throws on every mutation should not report otherwise.
    /// </summary>
    public bool IsReadOnly => _frozen;

    protected override void InsertItem(int index, T item)
    {
        ThrowIfFrozen();
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, T item)
    {
        ThrowIfFrozen();
        base.SetItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        ThrowIfFrozen();
        base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        ThrowIfFrozen();
        base.ClearItems();
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "these options are already in use and can no longer be changed; copy them with " +
                "new ErlSerializerOptions(existing) instead");
    }
}
