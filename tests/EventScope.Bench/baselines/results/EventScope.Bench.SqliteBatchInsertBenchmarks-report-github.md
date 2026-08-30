```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9278/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12500H 2.50GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method     | RowCount | Mean     | Error     | StdDev   | Gen0      | Gen1      | Allocated |
|----------- |--------- |---------:|----------:|---------:|----------:|----------:|----------:|
| **InsertRows** | **5000**     | **230.4 ms** | **188.26 ms** | **10.32 ms** |         **-** |         **-** |   **8.11 MB** |
| **InsertRows** | **50000**    | **362.3 ms** |  **71.26 ms** |  **3.91 ms** | **8000.0000** | **1000.0000** |  **80.65 MB** |
