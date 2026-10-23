# Manifest-pinned Preview isolation proof

This extends #4606 onto the installed native candidate. It does not replace that
PR's live `EndpointDataSource` route discovery. The external runner consumes its
maintained 23-route denominator, so adding a known route extends both fixtures.
Unknown route parameters fail instead of being skipped.

Run with Python 3 and Docker; supply the release manifest's immutable image and
full source SHA. The image must already be pulled. The runner verifies its OCI
revision label, creates its own PostGIS/Redis/network, uses a random synthetic
admin credential, and removes only those resources on exit. Ports bind only to
loopback. It does not build or modify the candidate.

```sh
python3 tests/dotnet/Honua.Server.Tests/Features/Alerts/Candidate/prove_isolation.py \
  --image ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd \
  --sha 7ba422672e0c751843b17beb36e954a019cc19fb \
  --output artifacts/alerts-candidate-3859
```

The output directory must be new. Failures exit nonzero and retain a failing
receipt and container logs. `SHA256SUMS` binds all output files. Credentials are
omitted from HTTP transcripts and redacted from logs. All recorded identifiers,
payloads, and destinations belong to this disposable fixture.

The independent expectations are:

- A single-tenant instance becomes ready and its authenticated administrator
  successfully reads the seeded zone (not a vacuous blanket authorization denial).
- Both tenant-header contexts receive the specific isolation-filter `403` on
  every known route. Unauthenticated tenant requests receive `401` or `403`.
- The seeded zone, rule, event, open lifecycle, dead-letter dispatch (three
  attempts), and paused webhook channel remain unchanged. State/checkpoint rows
  remain unchanged too. The square's area is independently `2 * 2 = 4`, its SRID
  is 4326, and every stored ordinate matches the input. Nodata is inapplicable.
- All 46 authenticated refusals each have a unique correlation ID and exactly
  one matching access-audit record (method/path/status), with no successful
  outcome, alert-domain mutation, or private instance service/destination details.
- With alerts explicitly enabled and opted in, default tenant resolution,
  explicit tenant resolution, and schema routing each terminate the native host
  with the specific isolation validator error before changing alert persistence.

## Acceptance boundary

The release promise retained by the 2026.1 Preview amendment is no cross-tenant
alert disclosure or mutation. These checks prove the installed refusal floor.
They do **not** qualify the issue's concurrent two-tenant evaluation scenario,
tenant-qualified persistence, channel/backlog/redrive, two signed receivers,
unauthorized JWT tenant claims, or all observability disclosure paths. HTTP
contexts here use an actual instance API key plus the supported tenant header;
the anonymous arm is not a tenant JWT.

The pinned image refuses the configuration needed for concurrent tenant workers;
its actual alert table columns are retained in the receipt. Producing two signed
tenant receiver transcripts from this image would require bypassing that guard
and inventing tenant ownership. Neither is a valid proof. This is a remaining
implementation/candidate qualification blocker, not a release of that criterion
and not a claim that the candidate is unavailable. Keep #3859 open.

The accompanying source-host TRX, when packaged, is explicitly separate evidence:
it executes the existing .NET startup and live route-discovery tests on the
recorded source revision, not inside the native candidate image.

## Retained execution

The [immutable archive](evidence/7ba4226-preview-isolation.tar.gz) contains the
manifest at release commit `f6c54b4396bdadb76676be7b839de71fb9a3de84`, native
image receipt (71 HTTP observations, 46 refusal audit records, three rejected
worker configurations), sanitized container logs, exact runner, and separately
identified source-host TRX (10 passed, zero failures/skips). `binding.json` records
the identities and qualification boundary. Both the archive and its entries have
SHA-256 checksums; verify the outer checksum from `evidence/` with
`sha256sum -c SHA256SUMS`.

Run the parser/audit regression checks with `python3 -m unittest discover -s
tests/dotnet/Honua.Server.Tests/Features/Alerts/Candidate -p 'test_*.py'`
(as one shell command). Six checks cover new HTTP methods, malformed routes,
a duplicate hiding a missing audit, mismatched request metadata, and both
permitted denied-access audit categories. The reviewed archive includes these
regression tests and the rerun with per-refusal correlation.
