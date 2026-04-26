# Cloud Docker Assets

This directory groups platform-specific files copied into cloud-targeted Docker images. These are deployment host shims, not shared application code.

## Azure Functions

`azure-functions/` is copied by `docker/Dockerfile.functions` and `docker/Dockerfile.functions.aot` into `/home/site/wwwroot/`. The files configure the Azure Functions custom-handler host and forward HTTP traffic to the published Honua server binary in `/home/site/wwwroot/app`.

## AWS Lambda

`lambda/` contains Lambda runtime support files. `docker/Dockerfile.lambda` copies `lambda/bootstrap.sh` into `/var/runtime/bootstrap` for the JIT, self-contained Lambda image that uses the Lambda Web Adapter.

The AOT Lambda Dockerfiles, `docker/Dockerfile.lambda.aot` and `docker/Dockerfile.lambda.aot.simple`, do not copy `lambda/bootstrap.sh`; they start `Honua.Server` directly with `ENTRYPOINT` or `CMD`.

Keep cloud-provider host shims here. Protocol implementations, query/edit pipelines, metadata behavior, telemetry, logging, caching, and authorization belong in `src/` and should remain shared across deployment targets.
