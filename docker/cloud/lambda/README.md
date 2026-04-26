# AWS Lambda Docker Assets

This folder contains Lambda-specific runtime support files copied into Lambda-targeted Docker images. It is not shared server code.

`docker/Dockerfile.lambda` publishes `src/Honua.Server` into `/var/task`, copies the Lambda Web Adapter into `/opt/extensions/lambda-adapter`, and copies `bootstrap.sh` into `/var/runtime/bootstrap`:

```dockerfile
COPY --from=build /app /var/task
COPY docker/cloud/lambda/bootstrap.sh /var/runtime/bootstrap
```

`bootstrap.sh` sets `ASPNETCORE_URLS` from Lambda Web Adapter's `PORT` value and starts `/var/task/Honua.Server`.

The AOT Lambda Dockerfiles start `Honua.Server` directly and do not use this bootstrap script.
