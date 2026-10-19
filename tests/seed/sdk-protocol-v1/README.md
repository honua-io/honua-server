# SDK protocol fixture overlay

This overlay supplies deterministic inputs for the restored .NET SDK geocoding,
Spec, scene, and routing calls under server #6405 and SDK #308. It does not
change the frozen 2026.1 core fixture. The overlay manifest binds the core
manifest bytes, routing seed, offline geocoder CSV, scene assets, server
defaults, and all-protocol build profile by SHA-256.

Run `python3 scripts/certification/verify-sdk-protocol-fixture.py` to check the
source inputs. `--server-env` emits the reviewed, nonsecret server settings;
`--client-env` emits the public SDK fixture IDs, Redlands geocoder query, and
six routing locations. Neither command asserts that a candidate uses them.

The **main server database** must provide pgRouting and PostGIS. For a
containerized database, bind its immutable image digest; for a managed database
such as RDS, bind the account, region, instance identity, engine/version,
extension versions, migration revision, and checked topology instead. The
default `postgis/postgis:16-3.4` image does not contain pgRouting. The
production `PgRoutingProvider` uses the server's shared Postgres connection;
the separate DB used by `PgRoutingFixture` is only a test substitute. Apply
the canonical migrations and the core fixture first.
The overlay routing seed then creates the 9-vertex, 12-edge lattice in the
same database. The `default` network dataset resolves `public.ways` and
`public.ways_vertices_pgr`.

The fixture producer requires an **ephemeral** target, `PGHOST`, `PGUSER`,
`PGPASSWORD`, `PGDATABASE`, `HONUA_ADMIN_PASSWORD`,
`HONUA_PROTOCOL_FIXTURE_TARGET=ephemeral`, a local-loopback HTTP or HTTPS
server URL, and a new receipt path. Run
`python3 scripts/certification/seed-sdk-protocol-fixture.py --apply --base-url
<ephemeral-server-url> --receipt <private-new-path>` after the candidate is
started with the reviewed settings. It applies the routing SQL, checks every
vertex/edge and both extensions, imports the two-row CSV through the supported
admin geocoding importer, and probes a real four-edge route and the authored
Redlands address. It does not print the database or admin credentials. Source
or setup drift aborts without a success receipt.

The setup receipt explicitly says `candidateQualified: false`: it does not
attest the actual server image and database deployment identity, configuration,
gRPC scene calls, Spec apply, or the SDK's 14 installed-client operations.
The release producer must independently bind the exact running image and
database image digest or managed-instance identity and versions, effective
config, migration revision and checked topology, source/core/overlay revisions,
tracked scene assets in the image, installed SDK package bytes, and every
operation result. `scene.catalog` remains experimental; fixture presence is
not a maturity claim. Missing or mismatched evidence remains a failing cell
under release #652.
