# Cloud Docker Assets

This directory groups platform-specific files copied into cloud-targeted Docker images. These are deployment host shims, not shared application code.

## AWS Lambda

`lambda/` contains Lambda runtime support files. `docker/Dockerfile.lambda` copies `lambda/bootstrap.sh` into `/var/runtime/bootstrap` for the JIT, self-contained Lambda image that uses the Lambda Web Adapter.

The AOT Lambda Dockerfile, `docker/Dockerfile.lambda.aot`, starts `Honua.Server` directly with `ENTRYPOINT`.

Keep cloud-provider host shims here. Protocol implementations, query/edit pipelines, metadata behavior, telemetry, logging, caching, and authorization belong in `src/` and should remain shared across deployment targets.
