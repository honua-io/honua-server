---
type: guide
title: "Client packages and registries"
description: "Every Honua client package is public. Which registry each one is on, and the version the quickstart pins."
resource: "https://hub.docker.com/r/honuaio/honua-server"
---
# Client packages and registries

**Every Honua client package is public and installs with no credentials**, and so
does the server image. Nothing on this page needs a token.

The [customer install manifest](https://honua.io/data/customer-install-manifest.json)
records the pins and where each comes from.

| Registry | Manifest pin | Role | Package-read credentials |
| --- | --- | --- | --- |
| PyPI | `honua-admin==0.1.8` | Control plane used by this journey | None |
| PyPI | `honua-sdk==0.1.11` | Data plane used by this journey | None |
| PyPI | `mcp==2.1.1` | Only if you drive the server over MCP | None |
| npm | `@honua/sdk-js@0.1.9-beta.0` | Alternative JS SDK and CLI | None |
| npm | `@honua/mcp-server@0.1.9-beta.0` | Alternative MCP proxy | None |
| NuGet | `Honua.Sdk` `1.6.4` | Alternative .NET SDK | None |

Pick the one for your language; you do not need the others. The npm and NuGet
pins are the release manifest's, not a claim that the Python quickstart exercised
them. The publication record beside the manifest identifies its immutable source
and byte hash.

## Installing the .NET client

`Honua.Sdk` and the rest of the `Honua.Sdk*` family are on nuget.org and install
anonymously:

```bash
dotnet new console
dotnet add package Honua.Sdk
```

No feed to add, no token, no package-source mapping. The same builds are also
mirrored to GitHub Packages, which does require a classic PAT with `read:packages`
and may need organization SSO authorization - use it only if you have a specific
reason to prefer that feed.

Return to the [quickstart](quickstart.md) for the Python installation journey.
