using BeamSharp.Serialization.Converters;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// A graph that refers back to itself used to recurse until the stack ran out, and a
/// <c>StackOverflowException</c> cannot be caught: the process went with it. These assert that the
/// write path reports the cycle instead — which is only testable at all because it no longer
/// overflows.
/// </summary>
public class CycleTests
{
    private static readonly ErlSerializerOptions Reflected = ErlReflection.Default;

    [Fact]
    public void An_object_that_points_at_itself_is_reported()
    {
        var link = new Link { Name = "a" };
        link.Next = link;

        var ex = Assert.Throws<ErlSerializationException>(() => ErlSerializer.Serialize(link, Reflected));
        Assert.Contains("refers back to itself", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Link), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_loop_between_two_objects_is_reported()
    {
        var a = new Link { Name = "a" };
        var b = new Link { Name = "b" };
        a.Next = b;
        b.Next = a;

        Assert.Throws<ErlSerializationException>(() => ErlSerializer.Serialize(a, Reflected));
    }

    [Fact]
    public void A_loop_that_closes_through_a_collection_is_reported()
    {
        var group = new Group { Name = "everyone" };
        group.Members.Add(new Group { Name = "child", Members = { group } });

        Assert.Throws<ErlSerializationException>(() => ErlSerializer.Serialize(group, Reflected));
    }

    [Fact]
    public void The_generated_path_reports_a_cycle_the_same_way()
    {
        var link = new Link { Name = "a" };
        link.Next = link;

        var reflected = Assert.Throws<ErlSerializationException>(
            () => ErlSerializer.Serialize(link, Reflected));
        var generated = Assert.Throws<ErlSerializationException>(
            () => ErlSerializer.Serialize(link, TestContext.Default));

        // Equivalence covers errors too, not just the terms that get written.
        Assert.Equal(reflected.Message, generated.Message);
    }

    [Fact]
    public void The_same_object_twice_is_not_a_cycle()
    {
        // Only an ancestor is a cycle. A value reachable by two paths is a tree once written, and
        // has to keep working -- guarding by "seen anywhere" would break every shared instance.
        var shared = new Person("Ada", 36);
        var value = new Nested(shared, [shared, shared], new Dictionary<string, int>());

        var map = Assert.IsType<ErlMap>(ErlSerializer.Serialize(value, Reflected));
        Assert.True(map.TryGetValue(new ErlAtom("friends"), out var friends));
        Assert.Equal(2, Assert.IsType<ErlList>(friends).Count);
    }

    [Fact]
    public void Nesting_deeper_than_the_encoder_allows_is_reported()
    {
        var head = new Link { Name = "0" };
        var tail = head;
        for (var i = 1; i <= WriteGuard.MaxDepth + 8; i++)
            tail = tail.Next = new Link { Name = i.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        var ex = Assert.Throws<ErlSerializationException>(() => ErlSerializer.Serialize(head, Reflected));
        Assert.Contains("nested more than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chain_within_the_limit_still_writes()
    {
        var head = new Link { Name = "0" };
        var tail = head;
        for (var i = 1; i < WriteGuard.MaxDepth - 8; i++)
            tail = tail.Next = new Link { Name = i.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        Assert.IsType<ErlMap>(ErlSerializer.Serialize(head, Reflected));
    }

    [Fact]
    public void A_failed_write_leaves_nothing_behind_for_the_next_one()
    {
        // The guard is thread-static, so an abandoned write has to unwind it. If it did not, the
        // objects left marked would be reported as cycles by every later write on this thread.
        var link = new Link { Name = "a" };
        link.Next = link;

        Assert.Throws<ErlSerializationException>(() => ErlSerializer.Serialize(link, Reflected));

        var ok = new Link { Name = "a", Next = new Link { Name = "b" } };
        Assert.Equal(
            ErlSerializer.Serialize(ok, Reflected),
            ErlSerializer.Serialize(ok, Reflected));
    }
}
