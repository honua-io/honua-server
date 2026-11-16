# Shared Postgres fixture migrations

Test hosts skip application migrations. Each `.sql.fixture` marker selects an
unchanged production SQL resource to apply during shared fixture initialization.
The marker contains its full `Honua.Server.Migrations.<filename>.sql` resource name.
MSBuild embeds markers automatically; no project or fixture list needs editing.

Markers sort by resource name: `1Stores` runs before `2ImportFunctions`, with
migration numbers determining order within each phase. Keep the zero-padded
migration filename in the marker name. The import functions run last because upload
tests also use a seed containing older import functions.

To select another migration, add its marker in the appropriate phase directory.
Use the canonical SQL resource; do not copy SQL into the test kit. Initialization
rejects missing resources and duplicate selections.
