```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9278/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12500H 2.50GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | PayloadSize | Mean     | Error     | StdDev   | Gen0        | Gen1        | Gen2        | Allocated |
|------------------------------ |------------ |---------:|----------:|---------:|------------:|------------:|------------:|----------:|
| **ReadOneThousandRandomPayloads** | **256**         | **359.2 ms** |  **49.73 ms** |  **2.73 ms** | **372000.0000** | **372000.0000** | **372000.0000** |   **1.73 GB** |
| **ReadOneThousandRandomPayloads** | **4096**        | **465.7 ms** | **590.05 ms** | **32.34 ms** | **489000.0000** | **488000.0000** | **488000.0000** |   **1.95 GB** |
