# Service folder rollout

Service folders are additive fields in Metadata v2 snapshots. The graph schema version remains
`2.0.0-alpha.1`. Folder membership belongs to the canonical graph, including deployments that
synthesize their initial graph from the legacy SQL catalog. The first folder mutation persists
and activates that graph. Protocol-specific `Route` values retain their meaning.

`Metadata.Name` remains the logical operator identity. `publicName` is an optional immutable
public leaf, defaulting to the logical name for existing services. `Metadata.Title` is mutable
display text. Folder mutations affect all facets of a logical service and confer no access.
A supplied public leaf supplies new-service display defaults without embedding an allocated
logical name. Publishing with omitted folder selection preserves existing membership; explicit
null selects root and conflicts with an existing non-root membership. Move that service first.

Folder and service listings return the snapshot ETag. Folder create, update, empty delete and
service move require that exact value as `If-Match`. Missing preconditions return 428 and stale
preconditions return 412. Refresh the listing before retrying a conflict. Folder slugs and actual
historical public service paths remain reserved, including after service removal or empty-folder
deletion. Aliases bind to the original facets, rather than to a reusable logical name.

Historical activation refuses a target that erases or rebinds current reservations. Recovery uses
the existing metadata release revert path (`CommitRevertAsync`), which saves a reconciled new
revision. Immutable historical documents and ETags are preserved. Stage similarly reconciles
against the current graph; a target staged before a later reservation may require restaging.

## Mixed-version deployment and rollback

Freeze every graph and catalog writer before the first old/new process overlap, including Admin,
MCP, imports, publishing, metadata releases, configuration writers and background catalog jobs.
Data queries and data edits that do not mutate catalog metadata may continue. Keep this freeze
through the entire rollback window; enable folder writes only after all catalog writers run the
new binary. An old writer accepts unknown JSON fields when reading, then drops them on Save or
Stage. Reader tolerance does not make old writers safe.

Back up a folder-bearing revision and retain its full graph document before rollback. The baseline
binary presents services flat and cannot route new folder-qualified URLs. After returning to the
new binary, recover via a reconciled revert revision before resuming catalog writes. Do not rewrite
a historical document beneath its retained ETag or activate an unsafe old target.

This model/Admin change does not advertise folder-qualified protocol URLs. Routing and discovery
must ship together in the next server change before folder paths are offered to public clients.
SDK and Console releases follow that routing contract.

## Binary compatibility validation

The opt-in `FolderSnapshotCompatibilityTests` use `HONUA_FOLDER_COMPAT_DIRECTORY` for snapshots
shared between the new and pre-folder binaries. Run `NewWriter_ProducesFolderSnapshotForBaselineReader`
with the new binary first. In a clean pre-folder checkout, copy the shared probe and
`FolderPostgresTestHost` into the Admin test directory, then copy
`tests/compatibility/metadata-folders/BaselineReader.cs.template` there as a `.cs` file. Run
`BaselineReader_ReadsNewSnapshotAndProvesSaveStageFieldLoss` against that checkout's product code.
Finally run `NewReader_ReadsBaselineSaveAndStageAsRootServices` with the new binary. Each phase
uses the real Postgres store and restarts the HTTP host over its persisted snapshot.

The recovery probe keeps logical and effective public identities unchanged to isolate folder
field loss and reconciled backup recovery. It does not make old-writer catalog mutations safe
or prove arbitrary identity recovery after violating the required write freeze.
