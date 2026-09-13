---
type: guide
title: "Registry clients and package credentials"
description: "The customer install manifest records the pins and their origins."
resource: "https://hub.docker.com/r/honuaio/honua-server"
---
# Registry clients and package credentials

The [customer install manifest](https://honua.io/data/customer-install-manifest.json)
records the pins and their origins. The quickstart uses the public PyPI control
and data clients; neither those packages nor the public server image requires
GitHub authentication.

| Registry | Manifest pin | Role | Package-read credentials |
| --- | --- | --- | --- |
| PyPI | `honua-admin==0.1.8` | Control plane used by this journey | None |
| PyPI | `honua-sdk==0.1.11` | Data plane used by this journey | None |
| PyPI | `mcp==2.1.1` | Transport for the server MCP import tool | None |
| npm | `@honua/sdk-js@0.1.9-beta.0` | Alternative JS SDK and CLI | None |
| npm | `@honua/mcp-server@0.1.4-beta.0` | Alternative MCP proxy | None |
| NuGet | `Honua.Sdk` `1.6.4` | Alternative .NET SDK | None |

npm and NuGet versions are the release manifest's existing alternative-client
pins, not a claim that this Python journey rehearsed those clients. Do not
install all ecosystems to complete the quickstart. The publication record beside
the manifest identifies its immutable source and byte hash.

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
