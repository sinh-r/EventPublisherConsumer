```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9278/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12500H 2.50GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | PayloadSize | Mean     | Error     | StdDev   | Gen0   | Allocated |
|------------------------------ |------------ |---------:|----------:|---------:|-------:|----------:|
| **ReadOneThousandRandomPayloads** | **256**         | **60.63 μs** | **10.709 μs** | **0.587 μs** | **3.4180** |  **31.62 KB** |
| **ReadOneThousandRandomPayloads** | **4096**        | **63.98 μs** |  **5.010 μs** | **0.275 μs** | **3.4180** |  **31.62 KB** |
