# clients/

Source for client-side projects that consume Honua server APIs and that
are intended to live in their own repositories long-term but are staged
here while their dedicated repo is being bootstrapped.

| Subdirectory | Future repo | Status |
| --- | --- | --- |
| [`qgis/`](./qgis) | `honua-io/honua-qgis` | First slice in progress (#808) |

When a project's home repo is created, the corresponding subdirectory
moves there in a single commit; nothing in `src/` or `docs/` references
these directories so the move is mechanical.

For first-class SDKs that already have their own repository (e.g.
`honua-sdk-dotnet`, `honua-sdk-js`, `honua-sdk-python`, `honua-mobile`),
see the related-repos list in `AGENTS.md`.
