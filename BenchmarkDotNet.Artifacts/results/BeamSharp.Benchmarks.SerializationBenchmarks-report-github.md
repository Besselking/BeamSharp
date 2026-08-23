```

BenchmarkDotNet v0.14.0, macOS 26.5.2 (25F84) [Darwin 25.5.0]
Apple M1 Pro, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.1026.32716), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.10 (10.0.1026.32716), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | Mean       | Ratio | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |-----------:|------:|-------:|-------:|----------:|------------:|
| ReadRecord_Reflection  |   399.5 ns |  1.02 | 0.1259 |      - |     792 B |        0.76 |
| ReadRecord_Generated   |   168.6 ns |  0.43 | 0.0279 |      - |     176 B |        0.17 |
| ReadNested_Reflection  | 1,851.3 ns |  4.71 | 0.5245 | 0.0019 |    3296 B |        3.17 |
| ReadNested_Generated   |   793.1 ns |  2.02 | 0.1764 |      - |    1112 B |        1.07 |
| WriteRecord_Reflection |   392.7 ns |  1.00 | 0.1655 |      - |    1040 B |        1.00 |
| WriteRecord_Generated  |   300.4 ns |  0.76 | 0.1426 |      - |     896 B |        0.86 |
| WriteNested_Reflection | 1,748.5 ns |  4.45 | 0.7324 | 0.0076 |    4600 B |        4.42 |
| WriteNested_Generated  | 1,424.5 ns |  3.63 | 0.6523 | 0.0057 |    4096 B |        3.94 |
