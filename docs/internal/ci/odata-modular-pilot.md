# OData modular composition pilot

This experiment compares the existing OData test assembly with the narrow
OData unit/component projects and runtime test host. The eighty existing unit
cases move to a dedicated OData Units shard so normal CI retains their coverage.
The new minimal-host component tests, image, and performance measurements remain
opt-in. Full solution validation, provider coverage, and AOT/image release gates
remain required. A green pilot is evidence for this composition only.

The driver is `scripts/ci/odata-pilot/pilot.py`. Its `plan` operation writes a
reviewable selection without restoring, evaluating MSBuild, building, testing,
or invoking Docker. Missing extracted projects are reported as blockers. The
unit selection names the thirteen unchanged classes moved to the unit assembly;
its identical filter is applied to the original baseline assembly. The planned component
measurements use the same six-contract class in both variants:
`Honua.Protocols.OData.ComponentTests.ODataContractTests`. The separate minimal
host isolation class is excluded from cross-host comparison.
The driver never duplicates or replaces the repository's affected-scope planner.

```bash
python3 scripts/ci/odata-pilot/pilot.py plan \
  --root /path/to/candidate --variant candidate --selection unit \
  --output /tmp/odata-unit-plan.json
python3 scripts/ci/odata-pilot/pilot.py plan \
  --root /path/to/candidate --variant candidate --selection component \
  --filter "$ODATA_COMPONENT_FILTER" --output /tmp/odata-component-plan.json
```

Use separate baseline and candidate worktrees, the same machine, SDK, composition
profile, package cache, database/Redis prerequisites, and test filter. Keep normal
build admission in place: run through the existing bounded supervisor/Flow build
reservation, never through an unreserved SDK path. Unit baseline uses `Honua.Protocols.OData.Tests`; unit candidate uses
`Honua.Protocols.OData.UnitTests`. Both component variants use the unchanged
`Honua.Protocols.OData.ComponentTests` contract runner: overlay that common test
infrastructure into the baseline worktree explicitly. The baseline component
graph is the union of the full server and contract runner; the candidate graph
is the contract runner and its minimal-host closure. Restore, clean/warm-up, and
build cover every selected entry project. The tool never compares unrelated old
integration cases with the new contracts.
The image baseline publishes `Honua.Server`; the candidate publishes
`tests/dotnet/Honua.OData.TestHost/Honua.OData.TestHost.csproj`.

For each selection, collect three paired repetitions of each scenario below.
Alternate which variant runs first between repetitions. Serialize runs so shared
machine pressure cannot masquerade as a graph improvement. Every output directory
must be new; retain unsuccessful attempts too.

- `cold-build-warm-packages`: restore first to warm packages, clean the selected
  transitive project outputs, then measure build and test startup. Restore and
  clean have separate timings; neither is counted as compilation time.
- `unchanged-incremental`: restore and establish an unedited warm build before
  timing the next build and test startup.
- `odata-only-edit`: establish that same warm build, append a harmless comment to
  one selected OData source, measure, and restore its exact original bytes in a
  `finally` block. Concurrent edits are preserved and require manual reconciliation.

Example of one candidate unit run; repeat for both variants, pairs 1–3, and all
three scenario names. Use the same relative edit path in both worktrees.

```bash
python3 scripts/ci/odata-pilot/pilot.py run \
  --root /path/to/candidate --variant candidate --selection unit \
  --scenario odata-only-edit --pair 1 \
  --edit-file src/Honua.Protocols.OData/Features/Example.cs \
  --output /tmp/odata-evidence/unit/edit/pair-1/candidate
```

The example edit filename is a placeholder: choose an actual existing OData
source in both worktrees. A stage has a bounded timeout (default 2,400 seconds).
Execution requires Linux descendant containment. The harness subreaps its own
children and checks PID start identities, including guardians which create new
sessions. On cancellation/timeout it terminates only those owned descendants and
waits for them before releasing source edits. If termination cannot be proven,
the harmless OData comment is retained and the run fails for manual reconciliation.
Cancellation/failure never certifies successful tests; inspect reservation
children before retrying an interrupted run. Do not edit a worktree being used
by another worker.

Each receipt contains the source commit and tracked-diff digest for provenance;
these global identifiers are deliberately excluded from the artifact key. The
key hashes the evaluated transitive restore graph, actual Compile/Content/
EmbeddedResource/AdditionalFiles/Analyzer inputs, linked fixture SQL/assets,
project/package/build imports, SDK, OS/kernel, architecture, configuration, and
composition. This includes files outside a project's directory. Generated bin/
obj outputs do not become source inputs. A change to another protocol outside
this closure does not invalidate the key; a shared build property or linked seed
change does. Multi-target graphs fail closed until per-framework evaluation is
implemented.

The harness always invokes the selected build before executing tests or
publishing. It does not restore an external build-output cache or trust another
receipt as proof that current outputs match. Consequently an artifact-key
mismatch never takes a `--no-build` shortcut: the current selected graph is built
first. Keep output artifacts alongside their receipt; any future cache consumer
must verify this full key rather than source SHA or project name alone.
The optional `identity` operation restores/evaluates, without compiling, and writes
`artifact.json` in a new output directory. Run it through existing admission to
obtain the input key before an external host controller generates its receipt:

```bash
python3 scripts/ci/odata-pilot/pilot.py identity \
  --root /path/to/candidate --variant candidate --selection component \
  --output /tmp/odata-component-identity
```

Timings cover restore, graph evaluation, warm-up/clean, build, test, publish, and
image stages. Build-to-first-test is the earliest identified TRX UTC start minus
the harness UTC build start, with clock consistency checks; its wall-clock basis
is recorded. TRX records retain qualified class/method and data-case identity,
result, start and duration. A smaller or different test set cannot qualify.

Peak process memory is a 100 ms sampled sum of owned subprocess-tree RSS; shared
pages may be counted more than once and short peaks may be missed. An exclusive
job-cgroup peak is explicitly unavailable rather than inferred from a shared
host cgroup. Queue waits come only from an isolated Flow invocation ledger;
otherwise they are null with a reason. Stage wall time includes any queue wait.
Promotion requires explicit zero queue wait for every build/test stage and
wall-clock versus monotonic-stage agreement within 100 ms. Unknown or nonzero
waits make the comparison ineligible rather than count as compilation time.
Flow ledger waits have whole-second resolution; retain this limitation when
interpreting very small incremental differences. Do not subtract guessed waits. Disk output bytes cover the selected closure's bin/obj trees. Runtime
image size comes from Docker inspect.

```bash
python3 scripts/ci/odata-pilot/pilot.py compare --selection unit \
  --receipt /tmp/odata-evidence/unit/cold/pair-1/baseline/receipt.json \
  --receipt /tmp/odata-evidence/unit/cold/pair-1/candidate/receipt.json \
  --output /tmp/odata-comparison.json
```

Supply all eighteen receipts (three pairs for each scenario) using repeated
`--receipt` arguments; the abbreviated command above intentionally cannot pass.
The comparison requires matching successful test identities/results and matching
SDK/platform/configuration/composition within every pair. Acceptance requires
median cold-build graph reduction ≥50%, build-to-first-test reduction ≥30%, and
peak process memory reduction ≥25%. Neither incremental scenario may regress in
any pair. Missing measurements, duplicate receipts, failed/skipped tests, or
insufficient repetitions produce a failed comparison, never a claimed win.

## Component host orchestration

Both measured component variants use external-host mode, with
`HONUA_ODATA_TEST_BASE_URL` and `HONUA_ODATA_TEST_ADMIN_KEY`. The default in-process
minimal host is useful for ordinary component validation, but is not used as a
substitute for a paired full-host comparison. Full and minimal hosts must be
externally provisioned with canonical `odata.yaml`, `OData__MaxPageSize=2`, and
`Limits__Edits__MaxPayloadSize=4096` before shared contracts run.

`run --selection component` currently fails at preflight before any subprocess
runs. Component `compare` also fails closed, including for previously produced
receipts. No installed controller proves live host ownership, freshness, endpoint
binding or test execution order; a handwritten or precreated receipt is not a
substitute. Completing that lifecycle implementation is an unfinished slice of
this pilot. Only unit paired measurements are independently executable today;
ordinary component validation remains available through its normal test project.

## Runtime-only image

The image selection publishes before invoking Docker. Its build context contains
only published runtime output and the pilot Dockerfile. It reads the ASP.NET
runtime digest directly from the main Dockerfile; no second pin is maintained.
The runtime context excludes environment-specific `appsettings.*.json` files
(including the baseline's development-only database password); excluded filenames
are recorded. Production defaults and runtime environment remain available.
The context rejects SDK/compiler files, nonempty JSON credential fields or
password-bearing connection strings, NuGet/npm credential configuration,
private keys and certificates. Plain `docker build` is deliberately not invoked:
`--builder-json` must name a managed builder argv array, and
`--builder-receipt` must name its new, service-generated evidence file.
The argv may substitute `{context}`, `{tag}`, `{runtime}`, `{assembly}`,
`{inputKey}`, and `{receipt}`; it is executed without shell evaluation.
The managed builder must use existing shared admission and a bounded BuildKit
worker, not an unrestricted host Docker daemon. Its receipt schema is
`honua.odata-pilot-managed-builder/v1`, with matching `inputKey`/`image`, a
`reservationId`, `sharedAdmission: true`, `boundedBuildKit: true`, successful
`exitCode`, measured positive `peakJobMemoryBytes`, start/end epochs within the
managed-command stage, and `limits` containing actual `memoryMaxBytes` ≤8 GiB,
`cpuQuotaPercent` ≤400, and `pidsMax` ≤512. These fields must come from the owned
service/cgroup and reservation evidence; manually writing asserted values is not
qualification. No builder adapter is installed or enabled by this pilot. A
missing adapter/receipt blocks image execution.

It copies no source, package cache, SDK, Docker
socket, or build-time credentials. The ASP.NET base supplies the runtime; user
1654 owns the application files. An image-build receipt alone is not component
or runtime qualification and is never accepted by the test comparison.

```bash
python3 scripts/ci/odata-pilot/pilot.py run \
  --root /path/to/candidate --variant candidate --selection image \
  --scenario cold-build-warm-packages --pair 1 \
  --builder-json /path/to/managed-builder-command.json \
  --builder-receipt /tmp/odata-evidence/image-builder-receipt.json \
  --output /tmp/odata-evidence/image/candidate
```

Provision canonical seeds, Postgres and Redis externally using the existing test
fixture/bootstrap surface. The pilot image does not seed databases. For a runtime
probe, set `HONUA_ODATA_PILOT_IMAGE`, `HONUA_ODATA_PILOT_CONNECTION`,
`HONUA_ODATA_PILOT_REDIS`, `HONUA_ODATA_PILOT_ADMIN_PASSWORD`, and
`HONUA_ODATA_PILOT_NETWORK` outside the image, then
use `docker compose -f docker/odata-pilot/compose.yaml up -d`. This requires an
existing isolated external network and caps the runtime at two CPUs, 1 GiB RAM
with no extra swap, and 128 PIDs. The filesystem is read-only except a 64 MiB
non-executable temporary directory; capabilities are dropped and privilege
escalation is disabled. Exercise the host through the component test/SDK surface,
retain that separate receipt, and remove only this pilot composition afterward.
Do not interpret image creation as a healthy route or seeded-store proof.

Run the lightweight tool tests without Docker or .NET:

```bash
python3 -m unittest discover -s scripts/ci/odata-pilot -p 'test_*.py'
```


### Orchestration adapter boundary

The current pilot has no qualified image builder or component host controller.
The interfaces above are acceptance contracts, not evidence that an installed
adapter already exists. Unit measurements can run independently; image and
component promotion stay blocked until their actual lifecycle receipts exist.

Flow exposes generic command supervision in
`honua-flow-work/tools/lib/build-reservation.py run` and canonical fair admission
in `tools/lib/build-slot-lib.sh` (`build_worktree_take`, then `build_slot_take`).
The guardian requires the already locked slot and worktree descriptors, their
canonical paths, graph key, parent PID, fresh reservation token and the real
command argv. It writes durable owner receipts before starting that command and
retains locks until its tracked Linux descendants drain. This is a supported
reservation primitive; renaming Docker commands to dotnet or npm is unnecessary
and does not provide containment.

Docker workers are processes of the Docker daemon, outside those descendants.
If a wrapper starts a detached BuildKit worker or host container and then dies,
the guardian can finish while that container still consumes resources. A shell
trap does not repair this failure mode. Therefore the existing primitive alone
cannot produce a genuine image or host receipt for this pilot.

A usable managed adapter needs these additional interfaces:

- A durable owner with canonical Flow reservation identity, actual physical
  memory admission, and crash recovery that removes only its recorded container
  IDs. Capacity must remain reserved until those containers have stopped, even
  after the requesting client disappears. Host and WSL available memory must
  remain separate measurements under the existing admission policy.
- An owned BuildKit worker with inspected memory/swap, CPU and PID limits before
  any build starts, a measured cgroup memory peak, immutable input/image binding,
  and verified teardown before recording completion. The supervisor's current
  `ImageBuild` requires a running image job and creates a 16 GiB worker; it does
  not implement this pilot's 8 GiB/PID/peak receipt contract. Cooperative admission
  currently rejects image jobs, so invoking that path is not an available bridge.
- A host controller that uses that same durable ownership model for the full
  baseline or minimal candidate runtime. It must accept an externally provisioned
  isolated canonical `odata.yaml` store, verify the matching runtime artifact,
  apply page limit 2 and edit payload limit 4096, measure startup and queue wait,
  and keep the host alive while the exact shared component class runs. It then
  tears down only its owned runtime and records the actual bounds and result.

The shared component runner accepts `HONUA_ODATA_TEST_BASE_URL` and
`HONUA_ODATA_TEST_ADMIN_KEY`; both variants use
`Honua.Protocols.OData.ComponentTests.ODataContractTests`. The baseline builds
its full server graph as well as that identical runner. The seed fixture and
standalone minimal host are not a durable container owner or generic reservation
service. Do not substitute manually asserted receipt fields for these missing
interfaces. Runtime Compose is a bounded manual probe and does not qualify as a
managed lifecycle receipt.


### Developer component tests without promotion measurements

Run the component project directly through
existing admitted .NET execution. Its default mode creates the minimal in-process
host and provisions canonical seeds with `PostgresFixture`; Docker must already
be available to that fixture. This does not require a promotion host receipt:

```bash
dotnet test tests/dotnet/Honua.Protocols.OData.ComponentTests/Honua.Protocols.OData.ComponentTests.csproj \
  --configuration Release \
  --filter FullyQualifiedName~Honua.Protocols.OData.ComponentTests.ODataContractTests
```

Use the canonical Flow .NET shim or an admitted supervisor job for that command
on a shared worker. For a separately managed full host, set
`HONUA_ODATA_TEST_BASE_URL` and `HONUA_ODATA_TEST_ADMIN_KEY` outside the source
before the same command; provision the canonical isolated store and required
limits first. The external mode runs the same six shared contracts. Its result
proves those contracts only; it does not prove measured resource improvements,
container lifetime containment, or pilot promotion. Candidate-only route
isolation lives in `ODataHostIsolationTests` and runs only with the minimal host.


Component `odata-only-edit` measurement is explicitly blocked. The harness edits
source before evaluating the graph and compiling; an ordinary `identity` receipt
and previously started host describe the unedited source and cannot match that
new input key. A future host-controller callback must receive the edited build
and its evaluated key before starting the matching runtime. Consequently the
complete 18-run component comparison cannot currently execute or promote. This
does not block direct developer component tests or unit measurements.


Pilot evaluation, restore, build and publish explicitly use
`IncludeSourceRevisionInInformationalVersion=false`,
`EnableSourceControlManagerQueries=false`, and `EnableSourceLink=false` in both
variants. These exact command properties are included in the artifact identity.
The installed .NET 10 SDK gates revision assembly metadata, source-control
initialization, and SourceLink generation on these properties. This controls
Git-derived metadata for the closure experiment: unrelated commits must not
silently change compiler outputs while retaining the same input key. Production
build properties are unchanged. The pilot still rebuilds before execution; it
does not claim byte-for-byte reproducibility or restore external binary caches.


For the Production runtime probe, provide a fresh connection-encryption master
key through `HONUA_ODATA_PILOT_MASTER_KEY` outside the image (mapped to
`Security__ConnectionEncryption__MasterKey`). Generate an ephemeral key using the
existing fixture/bootstrap surface and do not retain it in publish output or
receipts. Provide a strong, unique `HONUA_ODATA_PILOT_ADMIN_PASSWORD`; Production
requires at least 12 characters and rejects shipped placeholder passwords. The
runtime context check rejects nonempty JSON admin passwords, master keys,
private/signing/encryption keys, and the other recognized credential fields,
including nested objects and flattened configuration names.


Baseline unit comparisons require exactly the same selected cases as candidate,
including newly added storage-layer regressions. Overlay those tests and any
corresponding production bug fix onto the baseline so the comparison varies the
project/reference composition rather than test behavior. A baseline with only
the original 80 cases cannot be promoted against a candidate with an additional
regression; exact test identity comparison rejects that mismatch.

## Ownership and cleanup after delivery

Keep the pilot ticket, supervisor session, worker assignments, worktrees, and
validation receipts in the shared supervisor ledger. A submitted PR remains owned
until its required checks and review are resolved and its content reaches trunk.
Keep this pilot on its branch while the preceding delivery PR is still open;
refresh from trunk and consolidate the earlier OData unit prototype before opening
the next PR. Do not leave two unit projects claiming the same classes.

After merge, the assigned worker verifies the delivered content on trunk, archives
test and measurement receipts outside its worktree, and disposes only its recorded
Postgres databases, containers, and temporary image resources. Remove the worker's
worktree only after confirming it is clean, fully pushed, and no active reservation
uses it. Keep shared package caches. Record the delivering PR and trunk SHA, removed
resource IDs/paths, measured bytes reclaimed, and any retained evidence in the
ledger cleanup receipt. A failed cleanup stays visible for the supervisor or reaper
to retry; it is not a successful completion receipt. Unmerged branches and foreign
worktrees are not cleanup targets.

Image selection defaults to `--runtime linux-x64`; `--runtime linux-arm64` is
also supported. The image-only RID and `SelfContained=false` are applied
consistently to restore, graph evaluation, build and publish, and included in the
artifact identity. Publish remains `--no-build`; there is no second compilation.
Unit and component commands remain portable and reject `--runtime`.
The managed-builder argv supports `{platform}` (`linux/amd64` or `linux/arm64`)
and its receipt must record that same `platform`. The driver independently
checks the loaded image's Docker OS/architecture before accepting it. This avoids
packaging all RIDs' native assets or accepting an image built for a different
architecture; it does not unblock the missing managed builder implementation.


On the shared Flow worker, identity/run first use the canonical reservation
helper's read-only `key` operation to check MSBuild query argument support before
restore. Older installed parsers reject stock `-getItem:`/`-getProperty:` flags;
that capability failure blocks measurements until the Flow parser is reviewed
and updated. Graph evaluation uses the supported `/verbosity:quiet` spelling.
Do not bypass shared admission, rename the executable, or manually assert an
identity receipt to work around this prerequisite.


The parser preflight discovers only the selected PATH `dotnet` shim's real
`lane-bin/../lib/build-reservation.py`, or an explicitly configured
`HONUA_FLOW_RESERVATION_HELPER`. Other Flow checkouts do not govern the selected
executor. If an outer recording wrapper hides that shim, configure the helper
that wrapper actually delegates to; a missing explicit helper fails closed.


Artifact hashing excludes only the evaluated item's `ModifiedTime`,
`CreatedTime`, and `AccessedTime` filesystem metadata, as defined by
[MSBuild well-known item metadata](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-well-known-item-metadata).
Raw evaluation output remains in stage logs. Source bytes and all other item
metadata, including reference and copy behavior, remain identity inputs.


The evaluated graph follows ProjectReference items recursively, including
build-only analyzers omitted from NuGet's restore graph. Every discovered
project's evaluated inputs and source bytes contribute to identity and project
counts; cycles are deduplicated by canonical path. Missing or external projects,
multi-target graphs, and per-reference evaluation property overrides fail closed
rather than silently evaluating a different configuration.
