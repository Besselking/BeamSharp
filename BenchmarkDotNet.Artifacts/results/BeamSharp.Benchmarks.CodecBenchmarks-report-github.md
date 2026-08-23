```

BenchmarkDotNet v0.14.0, macOS 26.5.2 (25F84) [Darwin 25.5.0]
Apple M1 Pro, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.1026.32716), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.10 (10.0.1026.32716), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method               | Mean        | Gen0    | Gen1   | Allocated |
|--------------------- |------------:|--------:|-------:|----------:|
| Encode_GenServerCall |    128.8 ns |  0.0701 |      - |     440 B |
| Decode_GenServerCall |    233.2 ns |  0.1287 | 0.0002 |     808 B |
| Encode_200Maps       | 24,240.5 ns |  8.6975 | 0.5798 |   54840 B |
| Decode_200Maps       | 53,900.1 ns | 26.7334 | 8.6060 |  168080 B |
