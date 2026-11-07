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

GeoServices directory entries advertise the canonical folder/public leaf path. The root directory
lists root services and caller-visible folders; a folder lists its qualified service facets.
Historical aliases continue resolving their original facets but do not appear in discovery.
Empty and inaccessible folders are indistinguishable from unknown folders. Folder membership
does not grant service or resource access.

Named FeatureServer, MapServer, ImageServer, VectorTileServer, GPServer, NAServer and
VersionManagementServer prefixes use the same handlers and endpoint policies as root services.
Existing constrained numeric raster aliases and independently configured namespaces retain their
routes. SOAP catalog discovery and existing SOAP service prefixes follow the public path.
The optional `/arcgis` prefix and configured public base continue to apply to advertised links.
WMS and WMTS capabilities retain the incoming supported protocol base, including historical aliases.

Read-only GeoServices Admin probes accept a qualified folder/public leaf. A bare public leaf
resolves only when it identifies one logical service; an existing root reservation takes
precedence. Metadata-id and numeric-layer probes remain supported. These protocol probes are
separate from the control-plane Admin API, whose logical identities remain unchanged.

Native OGC identifiers, MCP resource/tool identities and Studio/Console matching keys stay stable.
Links targeting a GeoServices facet use its canonical public path; display labels use the title
or public leaf. Explicit folder discovery tools, SDK and Console authoring follow separately.
Source-folder import execution remains a separate change; the routing change does not promise
that import requests preserve or remap source folders.
