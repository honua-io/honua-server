# Honua.Benchmarks

BenchmarkDotNet harness for the server hot paths flagged by the pre-release
hardening audit (#1144). Runs the COG tile decompression dispatch, STAC
filter parsing, and OGC API datetime parsing as standalone microbenchmarks
so regressions surface in the dev loop instead of waiting for the nightly
soak. Each class is decorated with `[MemoryDiagnoser]` and a
`[BenchmarkCategory(...)]` so allocation regressions are caught alongside
ns/op and runs can be filtered by category.

Run with `dotnet run -c Release --project benchmarks/Honua.Benchmarks -- --filter '*'`,
or scope by category, e.g. `--anyCategories tile`. Pass `--list flat` to see
every benchmark without launching the runners.
