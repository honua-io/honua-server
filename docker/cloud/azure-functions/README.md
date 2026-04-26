# Azure Functions Docker Assets

This folder contains the Azure Functions host files that are copied into the Azure Functions container images. It is not a standalone Functions app source tree.

`docker/Dockerfile.functions` and `docker/Dockerfile.functions.aot` publish `src/Honua.Server` into `/home/site/wwwroot/app`, then copy this folder into `/home/site/wwwroot/`:

```dockerfile
COPY --from=build /out/app /home/site/wwwroot/app
COPY docker/cloud/azure-functions/ /home/site/wwwroot/
```

The copied files provide the custom-handler bridge used by the Azure Functions runtime:

- `host.json` configures the Functions host for a custom HTTP handler, removes the route prefix, and forwards/proxies HTTP requests to Honua.
- `handler.sh` starts `/home/site/wwwroot/app/Honua.Server` on `FUNCTIONS_CUSTOMHANDLER_PORT`.
- `Root/function.json` handles the empty root route.
- `Proxy/function.json` handles all other HTTP routes with `{*segments}`.

Keep target-specific Functions host assets here. Shared server code and protocol behavior belong under `src/`, not in this Docker support folder.
