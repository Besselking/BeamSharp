using System.Collections;
using System.Collections.Immutable;

namespace BeamSharp.Serialization.Generator;

/// <summary>
/// An immutable array that compares by value.
/// <para>
/// The incremental pipeline caches on model equality, and <see cref="ImmutableArray{T}"/> compares by
/// reference, so a model holding one would be considered changed on every keystroke and regenerate
/// needlessly. This exists purely so the models can be records that actually cache.
/// </para>
/// </summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> values)
    : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _values = values;

    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    public int Count => _values.IsDefault ? 0 : _values.Length;
    public T this[int index] => _values[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_values.IsDefault || other._values.IsDefault) return _values.IsDefault && other._values.IsDefault;
        if (_values.Length != other._values.Length) return false;

        for (var i = 0; i < _values.Length; i++)
            if (!_values[i].Equals(other._values[i]))
                return false;

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_values.IsDefault) return 0;

        var hash = 17;
        foreach (var value in _values) hash = hash * 31 + (value?.GetHashCode() ?? 0);
        return hash;
    }

    public IEnumerator<T> GetEnumerator() =>
        (_values.IsDefault ? Enumerable.Empty<T>() : _values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator EquatableArray<T>(ImmutableArray<T> values) => new(values);
}
