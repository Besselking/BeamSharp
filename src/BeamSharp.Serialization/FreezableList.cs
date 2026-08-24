using System.Collections;

namespace BeamSharp.Serialization;

/// <summary>
/// A list that refuses to change once the options holding it are frozen.
/// </summary>
/// <remarks>
/// <see cref="ErlSerializerOptions"/> refuses its scalar setters after <c>MakeReadOnly</c> because a
/// converter resolved under one configuration and cached must not then be asked to honour another.
/// A plain <see cref="List{T}"/> in a read-only property gives that guarantee away: the reference
/// cannot be replaced, but everything it holds can. This is what
/// <c>JsonSerializerOptions.Converters</c> does for the same reason.
/// </remarks>
internal sealed class FreezableList<T> : IList<T>
{
    private readonly List<T> _items;
    private bool _frozen;

    public FreezableList() => _items = [];

    public FreezableList(IEnumerable<T> items) => _items = [.. items];

    public void Freeze() => _frozen = true;

    public int Count => _items.Count;
    public bool IsReadOnly => _frozen;

    public T this[int index]
    {
        get => _items[index];
        set
        {
            ThrowIfFrozen();
            _items[index] = value;
        }
    }

    public void Add(T item)
    {
        ThrowIfFrozen();
        _items.Add(item);
    }

    public void Insert(int index, T item)
    {
        ThrowIfFrozen();
        _items.Insert(index, item);
    }

    public bool Remove(T item)
    {
        ThrowIfFrozen();
        return _items.Remove(item);
    }

    public void RemoveAt(int index)
    {
        ThrowIfFrozen();
        _items.RemoveAt(index);
    }

    public void Clear()
    {
        ThrowIfFrozen();
        _items.Clear();
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _items.IndexOf(item);
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "these options are already in use and can no longer be changed; copy them with " +
                "new ErlSerializerOptions(existing) instead");
    }
}
