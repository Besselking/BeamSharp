using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>
/// Tracks the objects a write is currently inside, so a graph that refers back to itself is
/// reported rather than recursed into until the stack runs out.
/// <para>
/// This has to be a guard rather than a depth check the caller can catch after the fact:
/// <c>StackOverflowException</c> cannot be caught and takes the process with it, so by the time an
/// unguarded cycle shows itself there is nothing left to report it to.
/// </para>
/// <para>
/// The state is thread-static because <see cref="ErlConverter{T}.Write"/> takes no per-operation
/// context to hang it on, and the whole write path is synchronous, so a thread is an operation.
/// </para>
/// </summary>
internal static class WriteGuard
{
    /// <summary>
    /// The same ceiling <see cref="TermEncoder"/> enforces. Matching it means the guard refuses
    /// nothing that would have encoded anyway, and refuses it here, where the CLR type is still
    /// known and the message can name it.
    /// </summary>
    public const int MaxDepth = TermEncoder.MaxDepth;

    [ThreadStatic] private static List<object>? _writing;

    /// <summary>
    /// Marks <paramref name="value"/> as being written, returning whether it was tracked. Pass that
    /// back to <see cref="Exit"/> from a <c>finally</c>, so an aborted write does not leave the
    /// value marked for the next one on this thread.
    /// </summary>
    public static bool Enter(object value, Type type)
    {
        // A boxed value type is a fresh reference every time it is written, so tracking one would
        // compare boxes rather than objects and never match. Strings are leaves. Neither can close
        // a cycle, and between them they are most of what a flat object writes.
        if (type.IsValueType || value is string) return false;

        var writing = _writing ??= new List<object>(capacity: 16);

        for (var i = 0; i < writing.Count; i++)
            if (ReferenceEquals(writing[i], value))
                throw new ErlSerializationException(
                    $"{type} refers back to itself, and an Erlang term is a tree: there is no way " +
                    $"to write a cycle. Break the cycle before serializing, or put [ErlIgnore] on " +
                    $"the member that closes it.");

        if (writing.Count >= MaxDepth)
            throw new ErlSerializationException(
                $"objects are nested more than {MaxDepth} deep at {type}; refusing to recurse " +
                $"further. A term that deep could not be encoded either.");

        writing.Add(value);
        return true;
    }

    /// <summary>Undoes an <see cref="Enter"/> that tracked its value.</summary>
    public static void Exit(bool entered)
    {
        if (entered) _writing!.RemoveAt(_writing.Count - 1);
    }
}
