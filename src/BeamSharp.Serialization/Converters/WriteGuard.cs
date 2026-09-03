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
    /// Marks <paramref name="value"/> as being written until the returned scope is disposed.
    /// <c>using</c> it is what keeps an aborted write from leaving the value marked for the next
    /// one on this thread.
    /// </summary>
    public static Scope Enter(object value, Type type)
    {
        // A boxed value type is a fresh reference every time it is written, so tracking one would
        // compare boxes rather than objects and never match. Strings are leaves. Neither can close
        // a cycle, and between them they are most of what a flat object writes.
        if (type.IsValueType || value is string) return default;

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
        return new Scope(writing);
    }

    /// <summary>
    /// Unmarks the value its <see cref="Enter"/> marked, or nothing at all when that call did not
    /// track one.
    /// <para>
    /// A ref struct so that it cannot outlive the frame that entered it: the list it pops is the
    /// entering thread's, so a scope stored in a field or carried across an await would unmark a
    /// value on whatever thread got there first. Nothing in the write path is async, and this is
    /// what keeps it that way by construction rather than by convention.
    /// </para>
    /// </summary>
    public readonly ref struct Scope
    {
        private readonly List<object>? _writing;

        internal Scope(List<object> writing) => _writing = writing;

        /// <summary>Pops the value this scope pushed. Disposing an untracked scope does nothing.</summary>
        public void Dispose() => _writing?.RemoveAt(_writing.Count - 1);
    }
}
