# Decoding microbenchmarks

Run from the repository root through `remote-run` on the build host:

```bash
remote-run bash -c 'python3 benchmarks/Honua.Decoding.Benchmarks/prepare-baseline.py && dotnet run --project benchmarks/Honua.Decoding.Benchmarks -c Release -- --filter "*" --artifacts artifacts/decoding-benchmarks'
```

The preparation script materializes the original production source at
`161420e43720e5baca66ba4e12130ed55226e6ed` into ignored artifacts, changing only
its namespace. Pass another commit to the script to compare a different baseline.
Both implementations run in the same BenchmarkDotNet process with allocation
diagnostics. Setup verifies identical point and raster outputs before measurement.
The project links the current production sources so benchmarks cannot drift from
the implementation. It is intentionally outside the server solution.

Point cases cover both byte orders. Raster cases cover 256×256 single-band, RGB
(scalar fallback), and RGBA tiles. The mutable buffers avoid measuring a tile copy;
each iteration performs the same modular arithmetic regardless of its input values.
These are short microbenchmarks on a shared host, not end-to-end throughput claims.
Keep the complete BenchmarkDotNet reports with validation artifacts.
