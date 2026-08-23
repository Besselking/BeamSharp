using BeamSharp.Terms;
using BenchmarkDotNet.Attributes;

namespace BeamSharp.Benchmarks;

/// <summary>Raw external term format throughput, which sets the ceiling for everything above it.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class CodecBenchmarks
{
    private ErlTerm _small = null!;
    private ErlTerm _large = null!;
    private byte[] _smallBytes = null!;
    private byte[] _largeBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        // The shape of a gen_server call: {'$gen_call', {Pid, [alias|Ref]}, {add, 1, 2}}.
        _small = Erl.Tuple(
            Erl.Atom("$gen_call"),
            Erl.Tuple(new ErlPid("caller@host", 42, 0, 7),
                Erl.ImproperList([Erl.Atom("alias")], new ErlRef("caller@host", 7, [1, 2, 3]))),
            Erl.Tuple(Erl.Atom("add"), Erl.Int(1), Erl.Int(2)));

        _large = new ErlList(Enumerable.Range(0, 200).Select(i => (ErlTerm)Erl.Map(
            ("id", Erl.Int(i)),
            ("name", Erl.String($"item-{i}")),
            ("tags", Erl.List(Erl.Atom("alpha"), Erl.Atom("beta"))))));

        _smallBytes = TermEncoder.Encode(_small);
        _largeBytes = TermEncoder.Encode(_large);
    }

    [Benchmark]
    public byte[] Encode_GenServerCall() => TermEncoder.Encode(_small);

    [Benchmark]
    public ErlTerm Decode_GenServerCall() => TermDecoder.Decode(_smallBytes);

    [Benchmark]
    public byte[] Encode_200Maps() => TermEncoder.Encode(_large);

    [Benchmark]
    public ErlTerm Decode_200Maps() => TermDecoder.Decode(_largeBytes);
}
