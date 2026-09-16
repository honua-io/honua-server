# SOAP services site-root replay (server#4974)

In ArcGIS Pro, adding an ArcGIS Server connection with the site-root URL failed
with "We were unable to connect". `GET /services` returned 404; only
`GET /services?wsdl` returned 200.

After the fix, the bare `GET /services` answers with the same service-catalog WSDL
that the `wsdl` query flag serves. `POST /services` at that address is unchanged.

## What was replayed

`replay-soap-services-site-root.py` needs only the Python standard library. It
requests three forms:

- `GET /services?wsdl`
- `GET /services`
- `GET /services/`

It compares the three bodies by SHA-256, confirms the root is `wsdl:definitions`,
and checks that the SOAP catalog at the same address still negotiates
`GetMessageVersion` in the 9.0 namespace ArcGIS Pro 3.7 uses.

```
python3 replay-soap-services-site-root.py <base-url> <receipt.json> --label <text>
```

## Targets

Both runs used the same PostGIS 16-3.4 database and private Redis, with
`ASPNETCORE_ENVIRONMENT=Production` and `Licensing__Mode=Disabled`.

| Receipt | Target | Checks passed |
| --- | --- | --- |
| `candidate-87966c3-before-fix.json` | Release-pinned candidate image: server `87966c3`, index `sha256:069f196bfa5c7201223d4d89868934242c4ace8805a6e48c122a88d84fa6eb1a`. This is the pre-fix control. | 2 / 5 |
| `branch-fix-4974-after-fix.json` | Diagnostic build of this branch: framework-dependent `Honua.Server` output on a .NET runtime image. `src/` matches the PR head. | 5 / 5 |

On the candidate, `?wsdl` returns 200, while `/services` and `/services/` return
404 with an empty body.

On the branch build, all three forms return 200 `text/xml` with the same body.
That body is byte-identical (SHA-256 `f5f58dafe4cb…`) to the WSDL the candidate
serves for `?wsdl`, so the WSDL itself is unchanged.

The pinned candidate cannot contain this fix. The post-fix run on a release image
happens when the candidate is re-pinned past the merge. Whether ArcGIS Pro's
site-root form now connects still has to be confirmed in a native Pro session.
