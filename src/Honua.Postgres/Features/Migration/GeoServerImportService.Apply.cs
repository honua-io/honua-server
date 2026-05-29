// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Apply-plan construction and execution for the GeoServer import: turns a
/// filtered source inventory into a deterministic
/// <see cref="MigrationApplyPlanArtifact"/>, executes each step against the
/// catalog (workspaces, data sources, styles, layer groups), and aggregates the
/// per-step results into the final <see cref="GeoServerImportResult"/>.
/// </summary>
internal sealed partial class GeoServerImportService
{
    private async Task<MigrationApplyPlanArtifact> CreateApplyPlanAsync(
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        var filteredServiceInfo = CreateFilteredServiceInfoForPlan(serviceInfo, filteredResources);
        var inventory = await BuildInventoryArtifactAsync(
                filteredServiceInfo,
                request.Username,
                request.Password,
                request.ImportStyles,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = MigrationManifestTranslator.Translate(
            inventory,
            new MigrationManifestTranslationOptions
            {
                TargetServiceName = serviceInfo.GlobalSettings?.Title ?? "geoserver-import"
            });

        return MigrationApplyPlanBuilder.Build(manifest);
    }

    private static GeoServerServiceInfo CreateFilteredServiceInfoForPlan(
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources)
    {
        var coverageStores = serviceInfo.CoverageStores
            .Where(store => filteredResources.Layers.Any(layer =>
                string.Equals(layer.WorkspaceName, store.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(layer.CoverageStoreName, store.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return serviceInfo with
        {
            Workspaces = filteredResources.Workspaces,
            DataStores = filteredResources.DataStores,
            CoverageStores = coverageStores,
            Layers = filteredResources.Layers,
            LayerGroups = filteredResources.LayerGroups,
            Styles = filteredResources.Styles
        };
    }

    private async Task<MigrationApplyExecutionArtifact> ExecuteApplyPlanAsync(
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        MigrationApplyPlanArtifact applyPlan,
        GeoServerImportProgress currentProgress,
        IProgress<GeoServerImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stepResults = new List<MigrationApplyExecutionStepResult>(applyPlan.Steps.Length);
        var layersById = filteredResources.Layers.ToDictionary(GetLayerId, StringComparer.Ordinal);
        var dataStoresByKey = filteredResources.DataStores.ToDictionary(
            static store => GetStoreKey(store.WorkspaceName, store.Name),
            StringComparer.OrdinalIgnoreCase);
        var layerGroupsById = filteredResources.LayerGroups.ToDictionary(GetLayerGroupId, StringComparer.Ordinal);
        // Slice 3 (#1015): style steps come out of the apply-plan builder with
        // Kind="style" and SourceId=GetStyleId(style). Build a lookup so the
        // apply path can resolve the underlying GeoServerStyleInfo (and its
        // SLD body) without re-running discovery.
        var stylesById = filteredResources.Styles.ToDictionary(GetStyleId, StringComparer.Ordinal);

        // Slice 1: apply workspace-level catalog entries first so that any subsequent
        // layer-group / layer apply can reference them. These records are deterministic
        // and idempotent — re-running the same manifest does not create duplicates.
        var workspaceStepResults = await ApplyWorkspaceCatalogStepsAsync(
                filteredResources,
                request,
                applyPlan,
                cancellationToken)
            .ConfigureAwait(false);
        stepResults.AddRange(workspaceStepResults);

        // Slice 2: apply data-source / data-store entries before per-layer steps so
        // each layer can reference an applied data source. Idempotent via PK on
        // (source_kind, source_id) in honua.migration_data_sources.
        var dataSourceStepResults = await ApplyDataSourceStepsAsync(
                filteredResources,
                request,
                applyPlan,
                cancellationToken)
            .ConfigureAwait(false);
        stepResults.AddRange(dataSourceStepResults);

        foreach (var step in applyPlan.Steps.OrderBy(static item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepResult = await ExecuteApplyStepAsync(
                    step,
                    layersById,
                    dataStoresByKey,
                    layerGroupsById,
                    stylesById,
                    applyPlan,
                    filteredResources,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            stepResults.Add(stepResult);

            progress?.Report(currentProgress with
            {
                Status = GeoServerImportStatus.Validating,
                ResourcesProcessed = stepResults.Count,
                CurrentPhase = $"Processed GeoServer migration step {stepResults.Count} of {applyPlan.Summary.TotalStepCount}"
            });
        }

        var summary = new MigrationApplyExecutionSummary
        {
            TotalStepCount = stepResults.Count,
            AppliedStepCount = stepResults.Count(static result => result.Outcome == "applied"),
            AlreadyAppliedStepCount = stepResults.Count(static result => result.Outcome == "already-applied"),
            ManualReviewStepCount = stepResults.Count(static result => result.Outcome == "manual-review"),
            UnsupportedStepCount = stepResults.Count(static result => result.Outcome == "unsupported"),
            FailedStepCount = stepResults.Count(static result => result.Outcome == "failed")
        };

        return new MigrationApplyExecutionArtifact
        {
            SourceKind = applyPlan.SourceKind,
            Source = applyPlan.Source,
            PlanFingerprint = applyPlan.PlanFingerprint,
            ReplayToken = applyPlan.ReplayToken,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Summary = summary,
            StepResults = stepResults.ToArray()
        };
    }

    private async Task<MigrationApplyExecutionStepResult> ExecuteApplyStepAsync(
        MigrationApplyPlanStep step,
        Dictionary<string, GeoServerLayerInfo> layersById,
        Dictionary<string, GeoServerDataStoreInfo> dataStoresByKey,
        Dictionary<string, GeoServerLayerGroupInfo> layerGroupsById,
        Dictionary<string, GeoServerStyleInfo> stylesById,
        MigrationApplyPlanArtifact applyPlan,
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        // Slice 3 (#1015): style steps still need to persist into the Honua
        // style catalog even when the manifest translator marks them
        // unsupported/manual-review, so the migration evidence pack carries
        // explicit conversion diagnostics rather than silently dropping the
        // source style. Dispatch to the style branch before the early
        // unsupported/manual-review returns below.
        if (string.Equals(step.Kind, "style", StringComparison.Ordinal))
        {
            return await ApplyStyleCatalogStepAsync(
                    step,
                    stylesById,
                    applyPlan,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (step.Disposition == "unsupported")
        {
            return CreateExecutionStepResult(
                step,
                "unsupported",
                "The source item is unsupported by the reviewed GeoServer migration plan.");
        }

        if (step.Disposition == "manual-review")
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The reviewed GeoServer migration plan requires operator review before this item can be applied.");
        }

        // Slice 1: persist layer-group catalog entries as Honua services. Data-copy,
        // style migration, and per-layer membership for layer groups are deferred to
        // follow-on slices and recorded as manual-review evidence by the manifest.
        if (string.Equals(step.Kind, "layer-group", StringComparison.Ordinal))
        {
            // Defensive scoping (#1098): reject layer-group catalog writes whose
            // owning workspace is not part of the operator's requested scope.
            if (layerGroupsById.TryGetValue(step.SourceId, out var scopeGroup) &&
                !IsWorkspaceInScope(filteredResources, scopeGroup.WorkspaceName))
            {
                Log.WorkspaceWriteRejected(_logger, "layer-group", scopeGroup.WorkspaceName ?? "global");
                return CreateExecutionStepResult(
                    step,
                    "manual-review",
                    $"Catalog write for layer-group '{step.SourceId}' rejected: source workspace '{scopeGroup.WorkspaceName}' is outside the requested workspace scope (issue #1098).");
            }

            return await ApplyLayerGroupCatalogStepAsync(
                    step,
                    layerGroupsById,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(step.Kind, "layer", StringComparison.Ordinal))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "This apply slice only publishes PostGIS-backed GeoServer feature layers; other item kinds are retained as review records.");
        }

        if (!layersById.TryGetValue(step.SourceId, out var layer))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer was not present in the filtered GeoServer discovery result.");
        }

        // Defensive scoping (#1098): reject layer catalog writes whose owning
        // workspace is not part of the operator's requested scope.
        if (!IsWorkspaceInScope(filteredResources, layer.WorkspaceName))
        {
            Log.WorkspaceWriteRejected(_logger, "layer", layer.WorkspaceName ?? "global");
            return CreateExecutionStepResult(
                step,
                "manual-review",
                $"Catalog write for layer '{step.SourceId}' rejected: source workspace '{layer.WorkspaceName}' is outside the requested workspace scope (issue #1098).");
        }

        if (string.IsNullOrWhiteSpace(layer.DataStoreName))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer is not backed by a GeoServer feature datastore; raster and virtual layers are not applied by this slice.");
        }

        if (!dataStoresByKey.TryGetValue(GetStoreKey(layer.WorkspaceName, layer.DataStoreName), out var dataStore))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer references a datastore that was not present in the filtered GeoServer discovery result.");
        }

        if (!IsPostGisDataStore(dataStore))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "Only PostGIS-backed GeoServer layers can be published directly to the Honua catalog by this apply slice.");
        }

        if (_layerPublishingService == null)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The target catalog publishing service is unavailable, so the layer remains a replayable apply-plan step.");
        }

        if (!TryResolveCatalogTarget(layer, dataStore, out var target, out var targetError))
        {
            return CreateExecutionStepResult(step, "manual-review", targetError);
        }

        // Slice 2 (#1015): when applyMode + catalog writer are configured, try to
        // copy feature data from the source PostGIS table into the Honua catalog
        // database before publishing. The copy is idempotent (skip when the
        // target table already exists with rows). On SourceMissing we fall
        // through to the original publish path which preserves slice-1 behavior.
        var publishSchema = target.Schema;
        var publishTable = target.Table;
        MigrationFeatureCopyOutcome? copyOutcome = null;
        if (request.ApplyMode && _catalogWriter != null && IsSafeIdentifier(layer.Name))
        {
            var copyTargetTable = BuildFeatureCopyTargetTable(target, layer);
            try
            {
                copyOutcome = await _catalogWriter.CopyFeatureDataAsync(
                        _connectionProvider.GetConnectionString(),
                        new MigrationFeatureCopyRequest
                        {
                            SourceSchema = target.Schema,
                            SourceTable = target.Table,
                            TargetSchema = "honua_data",
                            TargetTable = copyTargetTable
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (copyOutcome.Status == MigrationFeatureCopyStatus.Copied ||
                    copyOutcome.Status == MigrationFeatureCopyStatus.AlreadyApplied)
                {
                    publishSchema = "honua_data";
                    publishTable = copyTargetTable;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CreateExecutionStepResult(
                    step,
                    "failed",
                    $"Feature data copy for layer '{step.SourceId}' failed unexpectedly and requires operator review.");
            }
        }

        var publishRequest = new LayerPublishRequest
        {
            Schema = publishSchema,
            Table = publishTable,
            LayerName = step.TargetResourceName ?? layer.Name,
            Description = layer.Abstract,
            GeometryColumn = null,
            GeometryType = null,
            Srid = request.TargetSrid ?? ResolveSrid(layer.SRS),
            PrimaryKey = null,
            Fields = [],
            ServiceName = step.TargetServiceName,
            Enabled = request.AutoPublishLayers && layer.Enabled
        };

        try
        {
            var publishedLayer = await _layerPublishingService.PublishLayerAsync(
                    _connectionProvider.GetConnectionString(),
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            var copyNote = copyOutcome switch
            {
                { Status: MigrationFeatureCopyStatus.Copied } c =>
                    $" Copied {c.RowCount} feature rows into honua_data.{publishTable}.",
                { Status: MigrationFeatureCopyStatus.AlreadyApplied } c =>
                    $" Reused existing honua_data.{publishTable} ({c.RowCount} feature rows present).",
                _ => string.Empty
            };

            return CreateExecutionStepResult(
                step,
                "applied",
                $"Published catalog layer {publishedLayer.LayerId} from target table {publishSchema}.{publishTable}.{copyNote}",
                publishedLayer.LayerId);
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.Conflict)
        {
            return await HandleLayerPublishingConflictAsync(
                    step,
                    publishRequest,
                    ex,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.NotFound)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source table was not found in the target Honua database; data-copy apply is not implemented by this slice.");
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.Validation)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The target table failed catalog publication validation and requires operator review.");
        }
        catch (LayerPublishingException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply step failed in the target catalog publisher and requires operator review.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply step failed unexpectedly and requires operator review.");
        }
    }

    private async Task<MigrationApplyExecutionStepResult> HandleLayerPublishingConflictAsync(
        MigrationApplyPlanStep step,
        LayerPublishRequest publishRequest,
        LayerPublishingException exception,
        CancellationToken cancellationToken)
    {
        if (_layerPublishingService == null || !exception.LayerId.HasValue)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "A catalog layer for the target table already exists, but the publisher did not return the existing layer id needed to link it to the target service.");
        }

        try
        {
            var linkedLayer = await _layerPublishingService.LinkExistingLayerToServiceAsync(
                    _connectionProvider.GetConnectionString(),
                    exception.LayerId.Value,
                    publishRequest.ServiceName ?? "default",
                    publishRequest.Enabled,
                    cancellationToken)
                .ConfigureAwait(false);

            if (linkedLayer == null)
            {
                return CreateExecutionStepResult(
                    step,
                    "manual-review",
                    "A catalog layer for the target table already exists, but it could not be linked to the target service automatically.");
            }

            return CreateExecutionStepResult(
                step,
                "already-applied",
                $"Catalog layer {linkedLayer.LayerId} already exists for target table {publishRequest.Schema}.{publishRequest.Table} and is linked to target service {linkedLayer.ServiceName}.",
                linkedLayer.LayerId);
        }
        catch (LayerPublishingException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply conflict recovery failed in the target catalog publisher and requires operator review.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply conflict recovery failed unexpectedly and requires operator review.");
        }
    }

    /// <summary>
    /// Persist a catalog service entry for each filtered GeoServer workspace.
    /// </summary>
    /// <remarks>
    /// This is the slice-1 application of the migration manifest: workspaces become
    /// idempotent <c>honua.services</c> rows so that subsequent layer-group and layer
    /// apply steps can attach to them. Re-running the same manifest does not create
    /// duplicates because the underlying writer uses <c>ON CONFLICT DO NOTHING</c>.
    /// </remarks>
    private async Task<IReadOnlyList<MigrationApplyExecutionStepResult>> ApplyWorkspaceCatalogStepsAsync(
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        MigrationApplyPlanArtifact applyPlan,
        CancellationToken cancellationToken)
    {
        if (filteredResources.Workspaces.Length == 0)
        {
            return [];
        }

        var results = new List<MigrationApplyExecutionStepResult>(filteredResources.Workspaces.Length);
        foreach (var workspace in filteredResources.Workspaces.OrderBy(static w => w.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceId = GetWorkspaceId(workspace.Name);

            // Defensive scoping (#1098): even though FilterRequestedResources should
            // already exclude unscoped workspaces, re-check at the write site so a
            // future regression in plan/translator code cannot smuggle a
            // cross-workspace mutation into honua.services.
            if (!IsWorkspaceInScope(filteredResources, workspace.Name))
            {
                Log.WorkspaceWriteRejected(_logger, "workspace", workspace.Name);
                results.Add(CreateOutOfScopeStepResult(
                    sourceId: sourceId,
                    kind: "workspace",
                    targetServiceName: NormalizeCatalogServiceName(workspace.Name),
                    targetResourceName: workspace.Name,
                    workspaceName: workspace.Name));
                continue;
            }

            var targetServiceName = NormalizeCatalogServiceName(workspace.Name);
            var stepResult = await EnsureCatalogEntryAsync(
                    sourceId: sourceId,
                    kind: "workspace",
                    targetServiceName: targetServiceName,
                    targetResourceName: workspace.Name,
                    description: BuildWorkspaceDescription(workspace, applyPlan),
                    srid: request.TargetSrid ?? 4326,
                    applyMode: request.ApplyMode,
                    deferredMessage: "Workspace catalog persistence is deferred until applyMode=true and a catalog writer is configured.",
                    cancellationToken)
                .ConfigureAwait(false);
            results.Add(stepResult);
        }

        return results;
    }

    /// <summary>
    /// Persist a migration data-source row per filtered GeoServer data store.
    /// </summary>
    /// <remarks>
    /// Slice 2 of issue #1015. GeoServer data stores are recorded in the
    /// inventory as <c>ExternalDependency</c> entries — they do not appear as
    /// resource-level apply-plan steps. We therefore iterate the filtered data
    /// stores directly here so each apply run produces a deterministic step
    /// result with create / skip / error outcomes per data source. Persistence
    /// is idempotent via <c>ON CONFLICT (source_kind, source_id) DO NOTHING</c>.
    /// </remarks>
    private async Task<IReadOnlyList<MigrationApplyExecutionStepResult>> ApplyDataSourceStepsAsync(
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        MigrationApplyPlanArtifact applyPlan,
        CancellationToken cancellationToken)
    {
        if (filteredResources.DataStores.Length == 0)
        {
            return [];
        }

        // Workspace scope (#1098 / PR #1100): respect the operator's requested
        // workspace scope so unrelated workspaces' data stores are not applied.
        // When WorkspaceNames is set, only data stores in those workspaces are
        // eligible. The pre-existing FilterRequestedResources path keeps the
        // datastore array workspace-agnostic, so the filter is re-applied here.
        var scopedWorkspaceNames = request.WorkspaceNames is { Length: > 0 } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;

        var results = new List<MigrationApplyExecutionStepResult>(filteredResources.DataStores.Length);
        foreach (var dataStore in filteredResources.DataStores.OrderBy(static ds => GetDataStoreId(ds.WorkspaceName, ds.Name), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceId = GetDataStoreId(dataStore.WorkspaceName, dataStore.Name);
            var targetServiceName = NormalizeCatalogServiceName(
                string.IsNullOrWhiteSpace(dataStore.WorkspaceName)
                    ? $"datastore-{dataStore.Name}"
                    : $"{dataStore.WorkspaceName}-{dataStore.Name}");

            if (scopedWorkspaceNames is not null &&
                !string.IsNullOrWhiteSpace(dataStore.WorkspaceName) &&
                !scopedWorkspaceNames.Contains(dataStore.WorkspaceName))
            {
                results.Add(CreateExecutionStepResult(
                    new MigrationApplyPlanStep
                    {
                        Sequence = 0,
                        StepId = $"datasource:{sourceId}",
                        SourceId = sourceId,
                        Kind = "datastore",
                        Action = "apply-data-source",
                        Disposition = "manual-review",
                        TargetServiceName = targetServiceName,
                        TargetResourceName = dataStore.Name,
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "manual-review",
                            Reason = "Data-source apply rejected by workspace scope guard."
                        }
                    },
                    "manual-review",
                    $"Data-source apply for '{sourceId}' rejected: source workspace '{dataStore.WorkspaceName}' is outside the requested workspace scope (issue #1098)."));
                continue;
            }

            var syntheticStep = new MigrationApplyPlanStep
            {
                Sequence = 0,
                StepId = $"datasource:{sourceId}",
                SourceId = sourceId,
                Kind = "datastore",
                Action = "apply-data-source",
                Disposition = "ready",
                TargetServiceName = targetServiceName,
                TargetResourceName = dataStore.Name,
                Compatibility = new MigrationCompatibilityAssessment
                {
                    Level = "compatible",
                    Reason = "GeoServer data store is staged as an idempotent honua.migration_data_sources row."
                }
            };

            if (!request.ApplyMode || _catalogWriter == null)
            {
                results.Add(CreateExecutionStepResult(
                    syntheticStep,
                    "manual-review",
                    "Data-source persistence is deferred until applyMode=true and a catalog writer is configured."));
                continue;
            }

            var dataSourceType = ResolveDataSourceType(dataStore);
            try
            {
                var outcome = await _catalogWriter.EnsureDataSourceAsync(
                        _connectionProvider.GetConnectionString(),
                        new MigrationDataSourceRequest
                        {
                            SourceKind = applyPlan.SourceKind,
                            SourceId = sourceId,
                            DataSourceType = dataSourceType,
                            WorkspaceName = dataStore.WorkspaceName,
                            DisplayName = dataStore.Name,
                            ConnectionSummary = BuildDataSourceConnectionSummary(dataStore)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                var stepResult = outcome switch
                {
                    MigrationCatalogWriteOutcome.Created => CreateExecutionStepResult(
                        syntheticStep,
                        "applied",
                        $"Applied {dataSourceType} data source '{sourceId}' to honua.migration_data_sources."),
                    MigrationCatalogWriteOutcome.AlreadyExists => CreateExecutionStepResult(
                        syntheticStep,
                        "already-applied",
                        $"Data source '{sourceId}' already present; idempotent re-apply made no changes."),
                    _ => CreateExecutionStepResult(syntheticStep, "manual-review", "Catalog writer returned an unexpected outcome.")
                };
                results.Add(stepResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(CreateExecutionStepResult(
                    syntheticStep,
                    "failed",
                    $"Data-source apply for '{sourceId}' failed unexpectedly and requires operator review."));
            }
        }

        return results;
    }

    private static string ResolveDataSourceType(GeoServerDataStoreInfo dataStore)
    {
        if (!string.IsNullOrWhiteSpace(dataStore.Type))
        {
            return dataStore.Type;
        }

        if (dataStore.ConnectionParameters.TryGetValue("dbtype", out var dbTypeValue) &&
            dbTypeValue is string dbType &&
            !string.IsNullOrWhiteSpace(dbType))
        {
            return dbType;
        }

        return "unknown";
    }

    private static string BuildDataSourceConnectionSummary(GeoServerDataStoreInfo dataStore)
    {
        var host = TryGetStringParameter(dataStore.ConnectionParameters, "host");
        var database = TryGetStringParameter(dataStore.ConnectionParameters, "database");
        var schema = TryGetStringParameter(dataStore.ConnectionParameters, "schema");
        var path = TryGetStringParameter(dataStore.ConnectionParameters, "url")
            ?? TryGetStringParameter(dataStore.ConnectionParameters, "directory");

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(host))
        {
            parts.Add($"host={host}");
        }
        if (!string.IsNullOrWhiteSpace(database))
        {
            parts.Add($"database={database}");
        }
        if (!string.IsNullOrWhiteSpace(schema))
        {
            parts.Add($"schema={schema}");
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            parts.Add($"path={path}");
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// Persist a migration style row per filtered GeoServer style.
    /// </summary>
    /// <remarks>
    /// Slice 3 of issue #1015. Each style apply-plan step produces an
    /// idempotent row in <c>honua.migration_styles</c> with the original
    /// source body, any converter output, and structured conversion
    /// diagnostics. SLD styles are run through the registered
    /// <see cref="ISldStyleConverter"/> when available; conversion warnings
    /// and errors are persisted so operators can audit the deterministic
    /// migration outcome. When the converter reports errors (or when no
    /// converter is registered) the row is persisted with disposition
    /// <c>manual-review</c> — we do not claim perfect SLD visual parity
    /// when diagnostics report manual review (issue #1015 AC). Re-apply
    /// is idempotent via PK on (source_kind, source_id). Cross-workspace
    /// styles outside the operator's requested scope are rejected per
    /// issue #1098.
    /// </remarks>
    private async Task<MigrationApplyExecutionStepResult> ApplyStyleCatalogStepAsync(
        MigrationApplyPlanStep step,
        Dictionary<string, GeoServerStyleInfo> stylesById,
        MigrationApplyPlanArtifact applyPlan,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!stylesById.TryGetValue(step.SourceId, out var style))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source style was not present in the filtered GeoServer discovery result.");
        }

        // Workspace scope (#1098 / PR #1100): respect the operator's requested
        // workspace scope so unrelated workspaces' styles are not applied.
        if (request.WorkspaceNames is { Length: > 0 } scopedNames &&
            !string.IsNullOrWhiteSpace(style.WorkspaceName) &&
            !scopedNames.Contains(style.WorkspaceName, StringComparer.OrdinalIgnoreCase))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                $"Style apply for '{step.SourceId}' rejected: source workspace '{style.WorkspaceName}' is outside the requested workspace scope (issue #1098).");
        }

        if (!request.ApplyMode || _catalogWriter == null)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "Style catalog persistence is deferred until applyMode=true and a catalog writer is configured.");
        }

        var sourceFormat = string.IsNullOrWhiteSpace(style.Format) ? "sld" : style.Format.ToLowerInvariant();
        string? convertedBody = null;
        string? convertedFormat = null;
        var diagnostics = new List<StyleDiagnostic>();
        // Slice 3 (#1015): the manifest translator marks every SLD style as
        // "unsupported" because at inventory time we cannot know whether an
        // ISldStyleConverter is registered (issue #375 was the original gap).
        // The apply path now closes that gap by running the registered
        // converter and recording diagnostics, so the apply disposition must
        // reflect the converter's actual outcome rather than mirroring the
        // conservative manifest-level tag. We start optimistic ("applied")
        // and degrade to "manual-review" below when the converter is absent,
        // the SLD body is empty, conversion errors out, or the source format
        // has no deterministic converter (CSS, YSLD, etc.). Explicit
        // reviewer-driven manual-review hold-backs from the plan are still
        // respected so they are not silently promoted to applied.
        var disposition = string.Equals(step.Disposition, "manual-review", StringComparison.Ordinal)
            ? "manual-review"
            : "applied";

        if (string.Equals(sourceFormat, "sld", StringComparison.Ordinal))
        {
            // Reuse the existing ISldStyleConverter; do NOT reimplement converter
            // logic in the apply path. When the converter is not registered, the
            // style is still persisted but tagged manual-review with a converter
            // diagnostic so the evidence pack carries an explicit record.
            if (_sldConverter == null)
            {
                diagnostics.Add(new StyleDiagnostic(
                    "error",
                    "No ISldStyleConverter is registered; SLD style was persisted without conversion. Apply via the per-layer admin SLD endpoint to attach a MapLibre style."));
                disposition = "manual-review";
            }
            else if (string.IsNullOrWhiteSpace(style.SldContent))
            {
                diagnostics.Add(new StyleDiagnostic(
                    "error",
                    "SLD style has no embedded body; conversion was skipped."));
                disposition = "manual-review";
            }
            else
            {
                var conversion = _sldConverter.Convert(style.SldContent!);
                foreach (var warning in conversion.Warnings)
                {
                    diagnostics.Add(new StyleDiagnostic("warning", warning));
                }
                foreach (var error in conversion.Errors)
                {
                    diagnostics.Add(new StyleDiagnostic("error", error));
                }

                if (conversion.MapLibreLayersJson is { Length: > 0 } mapLibreJson)
                {
                    convertedBody = mapLibreJson;
                    convertedFormat = "maplibre-layers-json";
                }

                // Conversion produced no layers (HasErrors), or converter
                // emitted at least one error diagnostic: do not claim visual
                // parity. Per #1015 AC, mark the row manual-review.
                if (conversion.HasErrors)
                {
                    disposition = "manual-review";
                }
            }
        }
        else if (!string.Equals(sourceFormat, "mbstyle", StringComparison.Ordinal))
        {
            // Non-SLD/non-MapBox formats (CSS, YSLD, etc.) are persisted as
            // manual-review because Honua does not have a deterministic
            // converter for them.
            diagnostics.Add(new StyleDiagnostic(
                "error",
                $"Source style format '{sourceFormat}' is not supported by Honua's deterministic style converter; persisted as manual-review."));
            disposition = "manual-review";
        }

        var targetStyleId = BuildTargetStyleId(applyPlan.Source, style);

        try
        {
            var outcome = await _catalogWriter.EnsureStyleAsync(
                    _connectionProvider.GetConnectionString(),
                    new MigrationStyleRequest
                    {
                        SourceKind = applyPlan.SourceKind,
                        SourceId = step.SourceId,
                        TargetStyleId = targetStyleId,
                        StyleName = style.Name,
                        WorkspaceName = string.IsNullOrWhiteSpace(style.WorkspaceName) ? null : style.WorkspaceName,
                        SourceFormat = sourceFormat,
                        SourceLanguageVersion = string.IsNullOrWhiteSpace(style.LanguageVersion) ? null : style.LanguageVersion,
                        SourceBody = string.IsNullOrWhiteSpace(style.SldContent) ? null : style.SldContent,
                        ConvertedBody = convertedBody,
                        ConvertedFormat = convertedFormat,
                        DiagnosticsJson = SerializeStyleDiagnostics(diagnostics),
                        ReviewDisposition = disposition
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var diagnosticSummary = BuildStyleDiagnosticSummary(diagnostics);
            return outcome switch
            {
                MigrationCatalogWriteOutcome.Created when disposition == "manual-review" => CreateExecutionStepResult(
                    step,
                    "manual-review",
                    $"Persisted style '{step.SourceId}' with manual-review disposition. {diagnosticSummary} Do not claim visual parity until the diagnostics are resolved."),
                MigrationCatalogWriteOutcome.Created => CreateExecutionStepResult(
                    step,
                    "applied",
                    $"Applied {sourceFormat} style '{step.SourceId}' to honua.migration_styles.{(diagnosticSummary.Length > 0 ? " " + diagnosticSummary : string.Empty)}"),
                MigrationCatalogWriteOutcome.AlreadyExists => CreateExecutionStepResult(
                    step,
                    "already-applied",
                    $"Style '{step.SourceId}' already present; idempotent re-apply made no changes."),
                _ => CreateExecutionStepResult(step, "manual-review", "Catalog writer returned an unexpected outcome.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                $"Style apply for '{step.SourceId}' failed unexpectedly and requires operator review.");
        }
    }

    private static string BuildTargetStyleId(MigrationSourceIdentity source, GeoServerStyleInfo style)
    {
        var workspace = string.IsNullOrWhiteSpace(style.WorkspaceName) ? "global" : style.WorkspaceName;
        var service = NormalizeCatalogServiceName(source.DisplayName ?? source.BaseUrl ?? "geoserver-import");
        var styleName = NormalizeCatalogServiceName($"{workspace}-{style.Name}");
        return $"style:{service}:{styleName}";
    }

    private static string BuildFeatureCopyTargetTable(LayerCatalogTarget sourceTarget, GeoServerLayerInfo layer)
    {
        var sourceTable = string.IsNullOrWhiteSpace(sourceTarget.Table) ? layer.Name : sourceTarget.Table;
        var candidate = string.Equals(sourceTarget.Schema, "public", StringComparison.OrdinalIgnoreCase)
            ? layer.Name.ToLowerInvariant()
            : $"{sourceTarget.Schema}_{sourceTable}".ToLowerInvariant();

        if (candidate.Length <= PostgresIdentifierMaxLength)
        {
            return candidate;
        }

        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)).AsSpan(0, FeatureCopyTargetHashByteLength))
            .ToLowerInvariant();
        var prefixLength = PostgresIdentifierMaxLength - hash.Length - 1;
        return $"{candidate[..prefixLength]}_{hash}";
    }

    private static string SerializeStyleDiagnostics(IReadOnlyList<StyleDiagnostic> diagnostics)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var diagnostic in diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("severity", diagnostic.Severity);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildStyleDiagnosticSummary(IReadOnlyList<StyleDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }

        var warningCount = diagnostics.Count(static d => string.Equals(d.Severity, "warning", StringComparison.Ordinal));
        var errorCount = diagnostics.Count(static d => string.Equals(d.Severity, "error", StringComparison.Ordinal));
        return $"Recorded {errorCount} error and {warningCount} warning conversion diagnostic(s).";
    }

    private readonly record struct StyleDiagnostic(string Severity, string Message);

    private static bool IsWorkspaceInScope(FilteredResources filteredResources, string? workspaceName)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            // Global resources (no owning workspace) are not workspace-scoped and
            // are intentionally allowed through this guard.
            return true;
        }

        return filteredResources.ScopedWorkspaceNames.Contains(workspaceName);
    }

    private static MigrationApplyExecutionStepResult CreateOutOfScopeStepResult(
        string sourceId,
        string kind,
        string targetServiceName,
        string targetResourceName,
        string workspaceName)
    {
        var syntheticStep = new MigrationApplyPlanStep
        {
            Sequence = 0,
            StepId = $"catalog-{kind}:{sourceId}",
            SourceId = sourceId,
            Kind = kind,
            Action = "stage-catalog-entry",
            Disposition = "manual-review",
            TargetServiceName = targetServiceName,
            TargetResourceName = targetResourceName,
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "manual-review",
                Reason = "Catalog write rejected by workspace scope guard."
            }
        };

        return CreateExecutionStepResult(
            syntheticStep,
            "manual-review",
            $"Catalog write for {kind} '{sourceId}' rejected: source workspace '{workspaceName}' is outside the requested workspace scope (issue #1098).");
    }

    private async Task<MigrationApplyExecutionStepResult> ApplyLayerGroupCatalogStepAsync(
        MigrationApplyPlanStep step,
        Dictionary<string, GeoServerLayerGroupInfo> layerGroupsById,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!layerGroupsById.TryGetValue(step.SourceId, out var layerGroup))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer group was not present in the filtered GeoServer discovery result.");
        }

        // Slice 1 persists a dedicated catalog service per layer-group so each group has
        // a stable, idempotent identity. The translator's TargetServiceName is the
        // GeoServer-wide service identity, which is not unique per group; we synthesize
        // a workspace-qualified name instead.
        var targetServiceName = NormalizeCatalogServiceName(
            string.IsNullOrWhiteSpace(layerGroup.WorkspaceName)
                ? $"layergroup-{layerGroup.Name}"
                : $"{layerGroup.WorkspaceName}-{layerGroup.Name}");
        var description = BuildLayerGroupDescription(layerGroup);

        return await EnsureCatalogEntryFromStepAsync(
                step,
                targetServiceName,
                description,
                srid: request.TargetSrid ?? 4326,
                applyMode: request.ApplyMode,
                deferredMessage: "Layer-group catalog persistence is deferred until applyMode=true and a catalog writer is configured; layer membership and rendering remain manual review.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MigrationApplyExecutionStepResult> EnsureCatalogEntryAsync(
        string sourceId,
        string kind,
        string targetServiceName,
        string targetResourceName,
        string description,
        int srid,
        bool applyMode,
        string deferredMessage,
        CancellationToken cancellationToken)
    {
        var syntheticStep = new MigrationApplyPlanStep
        {
            Sequence = 0,
            StepId = $"catalog-{kind}:{sourceId}",
            SourceId = sourceId,
            Kind = kind,
            Action = "stage-catalog-entry",
            Disposition = "ready",
            TargetServiceName = targetServiceName,
            TargetResourceName = targetResourceName,
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = $"GeoServer {kind} is staged as an idempotent Honua catalog service entry by the apply slice."
            }
        };

        return await EnsureCatalogEntryFromStepAsync(
                syntheticStep,
                targetServiceName,
                description,
                srid,
                applyMode,
                deferredMessage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MigrationApplyExecutionStepResult> EnsureCatalogEntryFromStepAsync(
        MigrationApplyPlanStep step,
        string targetServiceName,
        string description,
        int srid,
        bool applyMode,
        string deferredMessage,
        CancellationToken cancellationToken)
    {
        if (!applyMode || _catalogWriter == null)
        {
            return CreateExecutionStepResult(step, "manual-review", deferredMessage);
        }

        try
        {
            var outcome = await _catalogWriter.EnsureCatalogServiceAsync(
                    _connectionProvider.GetConnectionString(),
                    new MigrationCatalogServiceRequest
                    {
                        ServiceName = targetServiceName,
                        Description = description,
                        Srid = srid,
                        EntryKind = step.Kind
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return outcome switch
            {
                MigrationCatalogWriteOutcome.Created => CreateExecutionStepResult(
                    step,
                    "applied",
                    $"Created Honua catalog service '{targetServiceName}' for GeoServer {step.Kind} '{step.SourceId}'."),
                MigrationCatalogWriteOutcome.AlreadyExists => CreateExecutionStepResult(
                    step,
                    "already-applied",
                    $"Honua catalog service '{targetServiceName}' already exists; idempotent re-apply made no changes."),
                _ => CreateExecutionStepResult(step, "manual-review", "Catalog writer returned an unexpected outcome.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                $"The catalog apply step for {step.Kind} '{step.SourceId}' failed unexpectedly and requires operator review.");
        }
    }

    private static string NormalizeCatalogServiceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "geoserver-import";
        }

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (ch is ' ' or '-' or '_' or '.' or ':' or '/')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
        }

        var normalized = builder.ToString().Trim('-');
        return normalized.Length == 0 ? "geoserver-import" : normalized;
    }

    private static string BuildWorkspaceDescription(
        GeoServerWorkspaceInfo workspace,
        MigrationApplyPlanArtifact applyPlan)
    {
        var summary = !string.IsNullOrWhiteSpace(workspace.Description)
            ? workspace.Description
            : $"GeoServer workspace '{workspace.Name}' staged by Honua migration apply slice 1.";
        return $"{summary} (source: {applyPlan.SourceKind})";
    }

    private static string BuildLayerGroupDescription(GeoServerLayerGroupInfo layerGroup)
    {
        var title = !string.IsNullOrWhiteSpace(layerGroup.Title) ? layerGroup.Title : layerGroup.Name;
        var workspaceQualifier = string.IsNullOrWhiteSpace(layerGroup.WorkspaceName)
            ? "global"
            : layerGroup.WorkspaceName;
        var description = !string.IsNullOrWhiteSpace(layerGroup.Abstract)
            ? layerGroup.Abstract
            : $"GeoServer layer group '{title}' in workspace '{workspaceQualifier}' staged by Honua migration apply slice 1.";
        return description;
    }

    private static MigrationApplyExecutionStepResult CreateExecutionStepResult(
        MigrationApplyPlanStep step,
        string outcome,
        string message,
        int? honuaLayerId = null)
        => new()
        {
            StepId = step.StepId,
            SourceId = step.SourceId,
            Kind = step.Kind,
            Action = step.Action,
            Disposition = step.Disposition,
            Outcome = outcome,
            Message = message,
            TargetServiceName = step.TargetServiceName,
            TargetResourceName = step.TargetResourceName,
            HonuaLayerId = honuaLayerId,
            ReviewCodes = step.ReviewCodes
        };

    private static bool IsPostGisDataStore(GeoServerDataStoreInfo dataStore)
        => string.Equals(dataStore.Type, "PostGIS", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(dataStore.Type, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
           (dataStore.ConnectionParameters.TryGetValue("dbtype", out var dbTypeValue) &&
            dbTypeValue is string dbType &&
            (string.Equals(dbType, "postgis", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(dbType, "postgres", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(dbType, "postgresql", StringComparison.OrdinalIgnoreCase)));

    private static string GetStoreKey(string workspaceName, string storeName)
        => $"{workspaceName}:{storeName}";

    private static bool TryResolveCatalogTarget(
        GeoServerLayerInfo layer,
        GeoServerDataStoreInfo dataStore,
        out LayerCatalogTarget target,
        out string error)
    {
        var schema = TryGetStringParameter(dataStore.ConnectionParameters, "schema") ?? "public";
        var table = string.IsNullOrWhiteSpace(layer.NativeName) ? layer.Name : layer.NativeName.Trim();

        if (TrySplitQualifiedTable(table, out var qualifiedSchema, out var qualifiedTable))
        {
            schema = qualifiedSchema;
            table = qualifiedTable;
        }

        if (!IsSafeIdentifier(schema) || !IsSafeIdentifier(table))
        {
            target = default;
            error = "The GeoServer native table name cannot be safely mapped to a target Honua catalog table.";
            return false;
        }

        target = new LayerCatalogTarget(schema, table);
        error = string.Empty;
        return true;
    }

    private static bool TrySplitQualifiedTable(string table, out string schema, out string tableName)
    {
        schema = string.Empty;
        tableName = string.Empty;

        var separatorIndex = table.IndexOf('.', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= table.Length - 1)
        {
            return false;
        }

        if (table.IndexOf('.', separatorIndex + 1) >= 0)
        {
            return false;
        }

        schema = table[..separatorIndex].Trim('"');
        tableName = table[(separatorIndex + 1)..].Trim('"');
        return true;
    }

    private static string? TryGetStringParameter(IReadOnlyDictionary<string, object> parameters, string key)
        => parameters.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool IsSafeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var ch in identifier)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static int? ResolveSrid(string? srs)
    {
        if (string.IsNullOrWhiteSpace(srs))
        {
            return null;
        }

        var separatorIndex = srs.LastIndexOf(':');
        var candidate = separatorIndex >= 0 ? srs[(separatorIndex + 1)..] : srs;
        return int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : null;
    }

    private static List<string> BuildApplyPlanWarnings(
        MigrationApplyPlanArtifact applyPlan,
        MigrationApplyExecutionArtifact applyExecution)
    {
        var warnings = new List<string>
        {
            "Non-dry-run GeoServer import generated a deterministic apply plan. Catalog mutation includes idempotent persistence of workspace and layer-group catalog entries, idempotent data-source registration and feature data copy for PostGIS sources, idempotent publication of PostGIS-backed layers, and idempotent persistence of style entries with structured SLD conversion diagnostics; WMS/WFS/WMTS service exposure changes remain explicit review records. Styles whose conversion diagnostics report errors are persisted with manual-review disposition — do not claim perfect SLD visual parity for those entries."
        };

        if (applyExecution.Summary.ManualReviewStepCount > 0)
        {
            warnings.Add($"{applyExecution.Summary.ManualReviewStepCount} apply execution steps require manual review before they can be considered applied.");
        }

        if (applyExecution.Summary.UnsupportedStepCount > 0 || applyPlan.Summary.UnsupportedItemCount > 0)
        {
            warnings.Add($"{Math.Max(applyExecution.Summary.UnsupportedStepCount, applyPlan.Summary.UnsupportedItemCount)} source items are unsupported by the current GeoServer apply path.");
        }

        if (applyExecution.Summary.AlreadyAppliedStepCount > 0)
        {
            warnings.Add($"{applyExecution.Summary.AlreadyAppliedStepCount} apply execution steps were already present in the target catalog and were treated as idempotent replays.");
        }

        if (applyExecution.Summary.FailedStepCount > 0)
        {
            warnings.Add($"{applyExecution.Summary.FailedStepCount} apply execution steps failed unexpectedly and require operator review.");
        }

        return warnings;
    }

    private static string BuildApplyFailureMessage(MigrationApplyExecutionArtifact applyExecution)
        => $"{applyExecution.Summary.FailedStepCount} GeoServer apply execution step(s) failed unexpectedly; inspect apply execution evidence before retrying.";

    private static MigrationRunMetricsArtifact BuildRunMetricsArtifact(
        MigrationRunMetricsRecorder recorder,
        GeoServerServiceInfo serviceInfo,
        string? runId,
        string measurementScope)
    {
        var sourceIdentity = new MigrationSourceIdentity
        {
            DisplayName = serviceInfo.GlobalSettings?.Title ?? "GeoServer",
            BaseUrl = serviceInfo.GeoServerRestUrl,
            Product = "GeoServer",
            Version = serviceInfo.Version,
            Build = serviceInfo.GitRevision ?? serviceInfo.BuildTimestamp,
            ServiceType = "REST"
        };

        return recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: sourceIdentity,
            measurementScope: measurementScope,
            runId: runId);
    }

    private void EmitRunMetrics(MigrationRunMetricsArtifact metrics)
    {
        Log.MigrationRunMetricsEmitted(
            _logger,
            metrics.SourceKind,
            metrics.SourceFamily,
            metrics.Totals.DurationMilliseconds ?? 0,
            metrics.Totals.SourceRequestCount ?? 0,
            metrics.Totals.ResourceCount ?? 0,
            metrics.Phases.Length);
    }

    private static GeoServerImportResult CreateApplyPlanResult(
        GeoServerServiceInfo serviceInfo,
        MigrationApplyPlanArtifact applyPlan,
        MigrationApplyExecutionArtifact applyExecution,
        GeoServerImportRequest request,
        TimeSpan duration,
        List<string> warnings)
        => GeoServerImportResult.CreateSuccess(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                sourceGeoServerVersion: serviceInfo.Version,
                duration: duration,
                warnings: warnings)
            with
        {
            ResourcesPlanned = applyPlan.Summary.TotalStepCount,
            ResourcesApplied = applyExecution.Summary.AppliedStepCount,
            ResourcesAlreadyApplied = applyExecution.Summary.AlreadyAppliedStepCount,
            ResourcesManualReview = applyExecution.Summary.ManualReviewStepCount,
            ResourcesUnsupported = applyExecution.Summary.UnsupportedStepCount,
            ApplyPlan = applyPlan,
            ApplyExecution = applyExecution
        };

    private static GeoServerImportResult CreateFailedApplyPlanResult(
        GeoServerServiceInfo serviceInfo,
        MigrationApplyPlanArtifact applyPlan,
        MigrationApplyExecutionArtifact applyExecution,
        GeoServerImportRequest request,
        TimeSpan duration,
        List<string> warnings,
        string failureMessage)
        => GeoServerImportResult.CreateFailure(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                failureMessage,
                duration)
            with
        {
            SourceGeoServerVersion = serviceInfo.Version,
            FailedResources = applyExecution.Summary.FailedStepCount,
            ResourcesPlanned = applyPlan.Summary.TotalStepCount,
            ResourcesApplied = applyExecution.Summary.AppliedStepCount,
            ResourcesAlreadyApplied = applyExecution.Summary.AlreadyAppliedStepCount,
            ResourcesManualReview = applyExecution.Summary.ManualReviewStepCount,
            ResourcesUnsupported = applyExecution.Summary.UnsupportedStepCount,
            Warnings = warnings,
            ApplyPlan = applyPlan,
            ApplyExecution = applyExecution
        };

    private readonly record struct LayerCatalogTarget(string Schema, string Table);
}
