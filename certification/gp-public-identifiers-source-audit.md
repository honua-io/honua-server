# Geoprocessing source audit

Source revision: `edb1771bb3115e3d0ecc04afe0c36c3f3b6f293b`.

Catalog source digest: `sha256:58a4303421d2363b977f58ac8f960a1698cd8aeaecb24cd2e3b8a4d5ec5d8945`.

The change under the catalog source roots is limited to three reads in
`ImportDatasetJobExecutor`: the publishing summary property is named
`StorageLayerId`. The log message, structured provenance metadata and provenance
artifact continue to carry the physical storage handle. The import artifact's
serialized `layerId` field is unchanged.

The affected catalog row is `import.dataset`. Catalog registration, capability
selection, destructive classification, cancellation and other executors are
unchanged. Its existing `proven` verdict and evidence body digest remain valid.

The recorded proof
`ImportDatasetJobExecutorTests.ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance`
ran against real PostGIS at the source revision above: one executed, one passed,
zero failed, zero skipped. It verifies three materialized features, published
storage metadata, a computed extent, terminal success, completed progress and
provenance whose layer handle matches the published storage handle.

Reproduce through the repository's build tooling:

```bash
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj -c Release \
  --filter FullyQualifiedName~ImportDatasetJobExecutorTests.ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance
```
