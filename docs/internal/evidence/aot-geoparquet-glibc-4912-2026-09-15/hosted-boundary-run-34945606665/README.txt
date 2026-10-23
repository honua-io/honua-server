Serving Image Boundary run 34945606665 (PR #4928 head 601a52e062)
canonical native-AOT (docker/Dockerfile.aot, linux-x64 glibc): dotnet publish -p:PublishAot=true succeeded; runtime link gate resolved ParquetSharpNative.so (libatomic.so.1 => /lib/x86_64-linux-gnu/libatomic.so.1), libduckdb.so and libSkiaSharp.so; the gate then stopped on libe_sqlite3.so (libm symbols bound by the host executable, standalone ldd -r false positive) -> removed from the gate.
Lambda native-AOT (docker/Dockerfile.lambda.aot + libatomic1 + Parquet/DuckDB gate): build and boundary verify succeeded.
Azure Functions native-AOT: succeeded (unchanged).
