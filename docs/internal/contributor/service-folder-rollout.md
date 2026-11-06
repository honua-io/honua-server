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

Folder operations follow publication scope: omit the optional `namespace` query parameter for
unscoped ownership; supply it to select the resolved tenant. Canonical operations preserve that
selection through approval replay. A namespace selects ownership and confers no access.

This model/Admin change does not advertise folder-qualified protocol URLs. Routing and discovery
must ship together in the next server change before folder paths are offered to public clients.
SDK and Console releases follow that routing contract.
