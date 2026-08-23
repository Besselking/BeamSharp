using BeamSharp.Serialization;
using BeamSharp.Terms;
using BenchmarkDotNet.Attributes;

namespace BeamSharp.Benchmarks;

/// <summary>
/// Generated converters against the reflection fallback, on the same objects.
/// <para>
/// The README claimed the generator was the faster path as well as the AOT-safe one. This is what
/// turns that into a number, and gives a regression somewhere to show up.
/// </para>
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class SerializationBenchmarks
{
    private static readonly ErlSerializerOptions Reflection = ErlReflection.Default;
    private static readonly ErlSerializerOptions Generated = BenchmarkTerms.Default.Options;

    private readonly Person _person = new("Ada", 36, "ada@example.com", Status.Active);
    private readonly Team _team = new(
        "core",
        [new Person("Ada", 36, "ada@example.com", Status.Active),
         new Person("Grace", 45, null, Status.OnLeave),
         new Person("Alan", 41, "alan@example.com", Status.Active)],
        new Dictionary<string, int> { ["ada"] = 1, ["grace"] = 2, ["alan"] = 3 });

    private ErlTerm _personTerm = null!;
    private ErlTerm _teamTerm = null!;

    [GlobalSetup]
    public void Setup()
    {
        _personTerm = ErlSerializer.Serialize(_person, Generated);
        _teamTerm = ErlSerializer.Serialize(_team, Generated);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("write-flat")]
    public ErlTerm WriteRecord_Reflection() => ErlSerializer.Serialize(_person, Reflection);

    [Benchmark, BenchmarkCategory("write-flat")]
    public ErlTerm WriteRecord_Generated() => ErlSerializer.Serialize(_person, Generated);

    [Benchmark, BenchmarkCategory("read-flat")]
    public Person ReadRecord_Reflection() => ErlSerializer.Deserialize<Person>(_personTerm, Reflection);

    [Benchmark, BenchmarkCategory("read-flat")]
    public Person ReadRecord_Generated() => ErlSerializer.Deserialize<Person>(_personTerm, Generated);

    [Benchmark, BenchmarkCategory("write-nested")]
    public ErlTerm WriteNested_Reflection() => ErlSerializer.Serialize(_team, Reflection);

    [Benchmark, BenchmarkCategory("write-nested")]
    public ErlTerm WriteNested_Generated() => ErlSerializer.Serialize(_team, Generated);

    [Benchmark, BenchmarkCategory("read-nested")]
    public Team ReadNested_Reflection() => ErlSerializer.Deserialize<Team>(_teamTerm, Reflection);

    [Benchmark, BenchmarkCategory("read-nested")]
    public Team ReadNested_Generated() => ErlSerializer.Deserialize<Team>(_teamTerm, Generated);
}

public enum Status
{
    Active,
    OnLeave
}

public record Person(string FirstName, int Age, string? Email, Status Status);

public record Team(string Name, List<Person> Members, Dictionary<string, int> Scores);

[ErlSerializable(typeof(Person))]
[ErlSerializable(typeof(Team))]
public partial class BenchmarkTerms : ErlSerializerContext;
