// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Translates discovered GeoServer configuration into deterministic migration manifests.
/// </summary>
internal sealed partial class GeoServerMigrationManifestService(
    IGeoServerImportService importService,
    ILogger<GeoServerMigrationManifestService> logger) : IGeoServerMigrationManifestService
{
    private const int DefaultPostgresPort = 5432;
    private const int MaxInlineStyleContentLength = 64 * 1024;
    private const string DefaultMetadataNamespace = "default";

    private readonly IGeoServerImportService _importService = importService ?? throw new ArgumentNullException(nameof(importService));
    private readonly ILogger<GeoServerMigrationManifestService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<MigrationManifest> TranslateAsync(
        GeoServerTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Log.TranslationStarting(_logger, request.GeoServerRestUrl);
        var stopwatch = Stopwatch.StartNew();
        using var activity = GeoServerImportActivity.StartTranslation(request.GeoServerRestUrl);

        try
        {
            var discoveryRequest = new GeoServerDiscoveryRequest
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Username = request.Username,
                Password = request.Password,
                TimeoutSeconds = request.RequestTimeoutSeconds,
                IncludeCompatibilityAnalysis = true,
                IncludeStyleContent = request.IncludeStyleContent
            };

            var serviceInfo = await _importService.DiscoverServiceAsync(discoveryRequest, cancellationToken).ConfigureAwait(false);
            var selected = GeoServerSelectionPlanner.Filter(
                serviceInfo,
                request.WorkspaceNames,
                request.DataStoreNames,
                request.LayerNames,
                request.ImportStyles);

            var manifest = BuildManifest(request, serviceInfo, selected);

            activity?.SetTag("migration.manifest.hash", manifest.ManifestHash);
            activity?.SetTag("migration.connection_draft.count", manifest.ConnectionDrafts.Count);
            activity?.SetTag("migration.publish_plan.count", manifest.PublishPlan.Count);
            activity?.SetTag("migration.style_plan.count", manifest.StylePlan.Count);
            activity?.SetTag("migration.diagnostic.count", manifest.Diagnostics.Count);
            activity?.SetTag("migration.manual_action.count", manifest.Summary.ManualActionCount);
            activity?.SetTag("migration.unsupported.count", manifest.Summary.UnsupportedCount);

            Log.TranslationCompleted(
                _logger,
                stopwatch.Elapsed,
                manifest.ConnectionDrafts.Count,
                manifest.PublishPlan.Count,
                manifest.StylePlan.Count,
                manifest.Diagnostics.Count,
                manifest.Summary.ManualActionCount,
                manifest.Summary.UnsupportedCount);

            return manifest;
        }
        catch (Exception ex)
        {
            Log.TranslationFailed(_logger, request.GeoServerRestUrl, ex);
            throw;
        }
    }

    private static MigrationManifest BuildManifest(
        GeoServerTranslationRequest request,
        GeoServerServiceInfo serviceInfo,
        GeoServerSelectedResources selected)
    {
        var defaultWorkspaceName = request.ImportOptions?.DefaultWorkspaceName ?? "geoserver-import";
        var diagnostics = new List<MigrationDiagnostic>();

        var translatorVersion = GetTranslatorVersion();
        var selection = new GeoServerMigrationSelection
        {
            WorkspaceNames = request.WorkspaceNames?.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            DataStoreNames = request.DataStoreNames?.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            LayerNames = request.LayerNames?.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            ImportStyles = request.ImportStyles,
            IncludeStyleContent = request.IncludeStyleContent,
            TargetSrid = request.TargetSrid,
            WorkspaceNameMappings = request.ImportOptions?.WorkspaceNameMappings != null
                ? new Dictionary<string, string>(
                    request.ImportOptions.WorkspaceNameMappings.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DefaultWorkspaceName = defaultWorkspaceName
        };

        var connectionDrafts = BuildConnectionDrafts(selected.DataStores, diagnostics);
        var publishPlan = BuildPublishPlan(request, selected, connectionDrafts, diagnostics);
        publishPlan = ApplyTargetLayerConflicts(publishPlan, diagnostics);
        var metadataResources = BuildMetadataResources(publishPlan, selected.Workspaces, diagnostics);
        var stylePlan = BuildStylePlan(request, selected.Layers, selected.Styles, diagnostics);

        AppendUnsupportedCoverageDiagnostics(selected.CoverageStores, diagnostics);
        AppendUnsupportedLayerGroupDiagnostics(selected.LayerGroups, diagnostics);

        var orderedDiagnostics = diagnostics
            .OrderBy(static diagnostic => diagnostic.SourceResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static diagnostic => diagnostic.SourceKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var summary = new MigrationManifestSummary
        {
            SelectedWorkspaceCount = selected.Workspaces.Length,
            SelectedDataStoreCount = selected.DataStores.Length,
            SelectedCoverageStoreCount = selected.CoverageStores.Length,
            SelectedLayerCount = selected.Layers.Length,
            SelectedLayerGroupCount = selected.LayerGroups.Length,
            SelectedStyleCount = stylePlan.Count,
            ConnectionDraftCount = connectionDrafts.Count,
            PublishPlanCount = publishPlan.Count,
            ReadyPublishPlanCount = publishPlan.Count(static entry => entry.EligibleForDirectPublish),
            MetadataResourceCount = metadataResources.Count,
            StylePlanCount = stylePlan.Count,
            DiagnosticCount = orderedDiagnostics.Length,
            ManualActionCount = CountManualActions(connectionDrafts, publishPlan, stylePlan, orderedDiagnostics),
            UnsupportedCount = CountUnsupported(publishPlan, stylePlan, orderedDiagnostics)
        };

        var manifest = new MigrationManifest
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TranslatorVersion = translatorVersion,
            SourceSummary = CreateSourceSummary(request.GeoServerRestUrl, serviceInfo, selected),
            Selection = selection,
            Summary = summary,
            ConnectionDrafts = connectionDrafts,
            PublishPlan = publishPlan,
            MetadataResources = metadataResources,
            StylePlan = stylePlan,
            Diagnostics = orderedDiagnostics
        };

        return manifest with
        {
            ManifestHash = MigrationManifestHasher.ComputeHash(manifest)
        };
    }

    private static List<MigrationConnectionDraft> BuildConnectionDrafts(
        IReadOnlyList<GeoServerDataStoreInfo> dataStores,
        List<MigrationDiagnostic> diagnostics)
    {
        var drafts = new List<MigrationConnectionDraft>();

        foreach (var dataStore in dataStores)
        {
            if (!IsSupportedPostGisDataStore(dataStore))
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Warning,
                    Code = MigrationReasonCodes.UnsupportedDatastoreType,
                    SourceResourceType = "DataStore",
                    SourceKey = GeoServerSelectionPlanner.GetQualifiedKey(dataStore.WorkspaceName, dataStore.Name),
                    Message = $"GeoServer datastore '{dataStore.WorkspaceName}/{dataStore.Name}' uses unsupported type '{dataStore.Type}'.",
                    ManualActions =
                    [
                        $"Manually migrate datastore '{dataStore.Name}' into a PostGIS-backed Honua connection before replay."
                    ]
                });
                continue;
            }

            var host = GetRequiredStringParameter(dataStore.ConnectionParameters, "host");
            var databaseName = GetRequiredStringParameter(dataStore.ConnectionParameters, "database");
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(databaseName))
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Error,
                    Code = MigrationReasonCodes.CreateSecureConnection,
                    SourceResourceType = "DataStore",
                    SourceKey = GeoServerSelectionPlanner.GetQualifiedKey(dataStore.WorkspaceName, dataStore.Name),
                    Message = $"GeoServer datastore '{dataStore.WorkspaceName}/{dataStore.Name}' is missing host or database metadata required for a secure connection draft.",
                    ManualActions =
                    [
                        $"Inspect datastore '{dataStore.Name}' in GeoServer and capture the missing connection settings before replay."
                    ]
                });
                continue;
            }

            var alias = GeoServerSelectionPlanner.GetQualifiedKey(dataStore.WorkspaceName, dataStore.Name);
            var schemaName = GetOptionalStringParameter(dataStore.ConnectionParameters, "schema");
            var usernameHint = GetOptionalStringParameter(dataStore.ConnectionParameters, "user");
            var sslMode = NormalizeSslMode(dataStore.ConnectionParameters);
            var draft = new MigrationConnectionDraft
            {
                Alias = alias,
                Host = host,
                Port = GetOptionalIntParameter(dataStore.ConnectionParameters, "port") ?? DefaultPostgresPort,
                DatabaseName = databaseName,
                SchemaName = schemaName,
                UsernameHint = usernameHint,
                SslMode = sslMode,
                SourceWorkspace = dataStore.WorkspaceName,
                SourceDataStore = dataStore.Name,
                Status = MigrationPlanStatus.ManualActionRequired,
                SecretRequirements =
                [
                    new MigrationSecretRequirement
                    {
                        Kind = MigrationSecretRequirementKind.Password,
                        Description = $"Supply the database password when creating Honua secure connection '{alias}'."
                    }
                ]
            };

            drafts.Add(draft);
            diagnostics.Add(new MigrationDiagnostic
            {
                Severity = MigrationDiagnosticSeverity.Info,
                Code = MigrationReasonCodes.CreateSecureConnection,
                SourceResourceType = "DataStore",
                SourceKey = alias,
                Message = $"Create secure connection '{alias}' before replaying layers from datastore '{dataStore.Name}'.",
                ManualActions =
                [
                    $"Create a Honua secure connection named '{alias}' using host '{host}' and database '{databaseName}'.",
                    $"Provide the datastore password separately; source secrets are intentionally not echoed in the manifest."
                ]
            });
        }

        return drafts
            .OrderBy(static draft => draft.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MigrationPublishPlanEntry> BuildPublishPlan(
        GeoServerTranslationRequest request,
        GeoServerSelectedResources selected,
        IReadOnlyList<MigrationConnectionDraft> connectionDrafts,
        List<MigrationDiagnostic> diagnostics)
    {
        var draftsByAlias = connectionDrafts.ToDictionary(static draft => draft.Alias, StringComparer.OrdinalIgnoreCase);
        var entries = new List<MigrationPublishPlanEntry>();

        foreach (var layer in selected.Layers)
        {
            var sourceLayerKey = GeoServerSelectionPlanner.GetQualifiedKey(layer.WorkspaceName, layer.Name);

            if (!string.IsNullOrWhiteSpace(layer.CoverageStoreName))
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Warning,
                    Code = MigrationReasonCodes.UnsupportedLayerSource,
                    SourceResourceType = "Layer",
                    SourceKey = sourceLayerKey,
                    Message = $"Layer '{sourceLayerKey}' is backed by coverage store '{layer.CoverageStoreName}' and is outside the vector translation scope.",
                    ManualActions =
                    [
                        $"Plan a raster-specific migration workflow for layer '{layer.Name}'."
                    ]
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(layer.DataStoreName))
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Warning,
                    Code = MigrationReasonCodes.UnsupportedLayerSource,
                    SourceResourceType = "Layer",
                    SourceKey = sourceLayerKey,
                    Message = $"Layer '{sourceLayerKey}' does not expose a vector datastore reference in GeoServer discovery.",
                    ManualActions =
                    [
                        $"Inspect layer '{layer.Name}' in GeoServer and publish it manually if it is backed by a supported PostGIS source."
                    ]
                });
                continue;
            }

            var connectionAlias = GeoServerSelectionPlanner.GetQualifiedKey(layer.WorkspaceName, layer.DataStoreName);
            if (!draftsByAlias.ContainsKey(connectionAlias))
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Warning,
                    Code = MigrationReasonCodes.UnsupportedLayerSource,
                    SourceResourceType = "Layer",
                    SourceKey = sourceLayerKey,
                    Message = $"Layer '{sourceLayerKey}' references datastore '{layer.DataStoreName}', which is not available as a PostGIS connection draft.",
                    ManualActions =
                    [
                        $"Migrate datastore '{layer.DataStoreName}' into PostGIS before replaying layer '{layer.Name}'."
                    ]
                });
                continue;
            }

            var targetServiceName = MapTargetServiceName(layer.WorkspaceName, request.ImportOptions);
            var targetLayerName = layer.Name;
            var sourceTableName = ExtractSourceTableName(layer);
            var sourceSchemaName = ExtractSourceSchemaName(layer, selected.DataStores);
            var sourceSrid = ParseSrid(layer.SRS);
            var planDiagnosticCodes = new List<string>();
            var manualActions = new List<string>();

            if (string.IsNullOrWhiteSpace(sourceSchemaName))
            {
                planDiagnosticCodes.Add(MigrationReasonCodes.ResolveSourceSchema);
                manualActions.Add($"Confirm the source schema for layer '{sourceLayerKey}' before replay.");
            }

            if (string.IsNullOrWhiteSpace(layer.GeometryType))
            {
                planDiagnosticCodes.Add(MigrationReasonCodes.MissingGeometryType);
                manualActions.Add($"Confirm the geometry type for layer '{sourceLayerKey}' before replay.");
            }

            if (sourceSrid == null)
            {
                planDiagnosticCodes.Add(MigrationReasonCodes.ResolveAmbiguousSrid);
                manualActions.Add($"Resolve the source SRID for layer '{sourceLayerKey}' before replay.");
            }

            if (request.TargetSrid.HasValue &&
                sourceSrid.HasValue &&
                request.TargetSrid.Value != sourceSrid.Value)
            {
                planDiagnosticCodes.Add(MigrationReasonCodes.UnsupportedTargetSridTransform);
                manualActions.Add(
                    $"Replay layer '{sourceLayerKey}' without reprojection or implement a transform-aware apply workflow before targeting SRID {request.TargetSrid.Value}.");
            }

            var isReady = planDiagnosticCodes.Count == 0;
            entries.Add(new MigrationPublishPlanEntry
            {
                SourceLayerKey = sourceLayerKey,
                SourceWorkspace = layer.WorkspaceName,
                SourceLayerName = layer.Name,
                SourceDataStore = layer.DataStoreName,
                SourceSchemaName = sourceSchemaName,
                SourceTableName = sourceTableName,
                GeometryColumn = layer.GeometryColumn,
                GeometryType = layer.GeometryType,
                SourceSrid = sourceSrid,
                TargetSrid = request.TargetSrid,
                ConnectionAlias = connectionAlias,
                TargetServiceName = targetServiceName,
                TargetLayerName = targetLayerName,
                EligibleForDirectPublish = isReady,
                Status = isReady ? MigrationPlanStatus.Ready : MigrationPlanStatus.ManualActionRequired,
                DiagnosticCodes = planDiagnosticCodes
            });

            foreach (var code in planDiagnosticCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = code == MigrationReasonCodes.UnsupportedTargetSridTransform
                        ? MigrationDiagnosticSeverity.Warning
                        : MigrationDiagnosticSeverity.Error,
                    Code = code,
                    SourceResourceType = "Layer",
                    SourceKey = sourceLayerKey,
                    Message = code switch
                    {
                        MigrationReasonCodes.ResolveSourceSchema =>
                            $"Layer '{sourceLayerKey}' is missing a source schema name in GeoServer discovery.",
                        MigrationReasonCodes.MissingGeometryType =>
                            $"Layer '{sourceLayerKey}' is missing geometry type metadata in GeoServer discovery.",
                        MigrationReasonCodes.ResolveAmbiguousSrid =>
                            $"Layer '{sourceLayerKey}' is missing a parseable SRID in GeoServer discovery.",
                        MigrationReasonCodes.UnsupportedTargetSridTransform =>
                            $"Layer '{sourceLayerKey}' requested target SRID {request.TargetSrid} but source SRID {sourceSrid} would require a transform outside the current scope.",
                        _ => $"Layer '{sourceLayerKey}' requires manual action."
                    },
                    ManualActions = manualActions
                });
            }
        }

        return entries
            .OrderBy(static entry => entry.SourceLayerKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MigrationPublishPlanEntry> ApplyTargetLayerConflicts(
        IReadOnlyList<MigrationPublishPlanEntry> publishPlan,
        List<MigrationDiagnostic> diagnostics)
    {
        var conflictingTargets = publishPlan
            .GroupBy(
                static entry => $"{entry.TargetServiceName}|{entry.TargetLayerName}",
                StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .ToDictionary(
                static group => group.Key,
                StringComparer.OrdinalIgnoreCase);

        if (conflictingTargets.Count == 0)
        {
            return publishPlan.ToList();
        }

        var updatedEntries = new List<MigrationPublishPlanEntry>(publishPlan.Count);
        foreach (var entry in publishPlan)
        {
            var targetKey = $"{entry.TargetServiceName}|{entry.TargetLayerName}";
            if (!conflictingTargets.ContainsKey(targetKey))
            {
                updatedEntries.Add(entry);
                continue;
            }

            diagnostics.Add(new MigrationDiagnostic
            {
                Severity = MigrationDiagnosticSeverity.Error,
                Code = MigrationReasonCodes.ConflictingTargetLayerName,
                SourceResourceType = "Layer",
                SourceKey = entry.SourceLayerKey,
                Message = $"Multiple translated layers map to target layer '{entry.TargetServiceName}/{entry.TargetLayerName}'.",
                ManualActions =
                [
                    $"Adjust workspace mappings or target layer names so '{entry.TargetServiceName}/{entry.TargetLayerName}' is unique."
                ]
            });

            var diagnosticCodes = entry.DiagnosticCodes.Contains(MigrationReasonCodes.ConflictingTargetLayerName, StringComparer.OrdinalIgnoreCase)
                ? entry.DiagnosticCodes
                : entry.DiagnosticCodes
                    .Append(MigrationReasonCodes.ConflictingTargetLayerName)
                    .ToArray();

            updatedEntries.Add(entry with
            {
                EligibleForDirectPublish = false,
                Status = MigrationPlanStatus.ManualActionRequired,
                DiagnosticCodes = diagnosticCodes
            });
        }

        return updatedEntries
            .OrderBy(static entry => entry.SourceLayerKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MetadataResource> BuildMetadataResources(
        IReadOnlyList<MigrationPublishPlanEntry> publishPlan,
        IReadOnlyList<GeoServerWorkspaceInfo> workspaces,
        List<MigrationDiagnostic> diagnostics)
    {
        var resources = new List<MetadataResource>();
        var workspaceLookup = workspaces.ToDictionary(static workspace => workspace.Name, StringComparer.OrdinalIgnoreCase);

        var layerIdentityGroups = publishPlan
            .Where(static plan => !string.IsNullOrWhiteSpace(plan.SourceSchemaName) &&
                                  !string.IsNullOrWhiteSpace(plan.GeometryType) &&
                                  plan.SourceSrid.HasValue)
            .GroupBy(
                static plan => $"{plan.TargetServiceName}|{plan.TargetLayerName}",
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in layerIdentityGroups)
        {
            var plans = group.ToArray();
            if (plans.Length != 1)
            {
                continue;
            }

            var plan = plans[0];
            if (plan.DiagnosticCodes.Contains(MigrationReasonCodes.ConflictingTargetLayerName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            resources.Add(CreateLayerMetadataResource(plan));
        }

        var servicePlanGroups = publishPlan
            .GroupBy(static plan => plan.TargetServiceName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in servicePlanGroups)
        {
            var srids = group
                .Where(static plan => plan.SourceSrid.HasValue)
                .Select(static plan => plan.SourceSrid!.Value)
                .Distinct()
                .ToArray();

            if (srids.Length != 1)
            {
                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Warning,
                    Code = MigrationReasonCodes.ConflictingServiceSrid,
                    SourceResourceType = "Service",
                    SourceKey = group.Key,
                    Message = $"Target service '{group.Key}' aggregates layers with conflicting or missing SRIDs.",
                    ManualActions =
                    [
                        $"Confirm the service-level SRID strategy for '{group.Key}' before applying service metadata."
                    ]
                });
                continue;
            }

            var representativeLayer = group.OrderBy(static plan => plan.SourceLayerKey, StringComparer.OrdinalIgnoreCase).First();
            workspaceLookup.TryGetValue(representativeLayer.SourceWorkspace, out var workspace);
            resources.Add(CreateServiceMetadataResource(group.Key, representativeLayer, workspace, srids[0]));
        }

        return resources
            .OrderBy(static resource => resource.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static resource => resource.Metadata?.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static resource => resource.Metadata?.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MigrationStylePlanEntry> BuildStylePlan(
        GeoServerTranslationRequest request,
        IReadOnlyList<GeoServerLayerInfo> layers,
        IReadOnlyList<GeoServerStyleInfo> styles,
        List<MigrationDiagnostic> diagnostics)
    {
        if (!request.ImportStyles)
        {
            return [];
        }

        var styleLookup = styles.ToDictionary(
            static style => GeoServerSelectionPlanner.GetQualifiedKey(style.WorkspaceName, style.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var style in styles.Where(static style => string.IsNullOrWhiteSpace(style.WorkspaceName)))
        {
            styleLookup.TryAdd(style.Name, style);
        }

        var entries = new List<MigrationStylePlanEntry>();
        foreach (var layer in layers)
        {
            var sourceLayerKey = GeoServerSelectionPlanner.GetQualifiedKey(layer.WorkspaceName, layer.Name);
            foreach (var reference in GetOrderedStyleReferences(layer))
            {
                if (!TryResolveStyle(reference, layer.WorkspaceName, styleLookup, out var style))
                {
                    continue;
                }

                var isSld = style.Format.Equals("sld", StringComparison.OrdinalIgnoreCase);
                var primaryDiagnosticCode = isSld
                    ? MigrationReasonCodes.UnsupportedSldStyle
                    : MigrationReasonCodes.UnsupportedStyleFormat;
                var entryDiagnosticCodes = new List<string>
                {
                    primaryDiagnosticCode,
                    MigrationReasonCodes.ManualStyleConversion
                };

                string? sourceContent = null;
                if (!string.IsNullOrWhiteSpace(style.SldContent))
                {
                    if (style.SldContent.Length <= MaxInlineStyleContentLength)
                    {
                        sourceContent = request.IncludeStyleContent ? style.SldContent : null;
                    }
                    else
                    {
                        entryDiagnosticCodes.Add(MigrationReasonCodes.StyleContentTooLarge);
                        diagnostics.Add(new MigrationDiagnostic
                        {
                            Severity = MigrationDiagnosticSeverity.Warning,
                            Code = MigrationReasonCodes.StyleContentTooLarge,
                            SourceResourceType = "Style",
                            SourceKey = $"{sourceLayerKey}:{style.Name}",
                            Message = $"Style '{style.Name}' was not embedded because its SLD content exceeds the manifest size budget.",
                            ManualActions =
                            [
                                $"Retrieve style '{style.Name}' directly from GeoServer when performing manual style conversion."
                            ]
                        });
                    }
                }

                entries.Add(new MigrationStylePlanEntry
                {
                    SourceLayerKey = sourceLayerKey,
                    SourceStyleName = style.Name,
                    SourceStyleWorkspace = style.WorkspaceName,
                    SourceFormat = style.Format,
                    TranslationStatus = isSld
                        ? MigrationStyleTranslationStatus.ManualActionRequired
                        : MigrationStyleTranslationStatus.Unsupported,
                    TargetStyleName = style.Name,
                    TargetFormat = null,
                    TargetStyle = null,
                    SourceReferenceUrl = BuildStyleReferenceUrl(request.GeoServerRestUrl, style),
                    SourceContent = sourceContent,
                    DiagnosticCodes = entryDiagnosticCodes
                });

                diagnostics.Add(new MigrationDiagnostic
                {
                    Severity = MigrationDiagnosticSeverity.Warning,
                    Code = primaryDiagnosticCode,
                    SourceResourceType = "Style",
                    SourceKey = $"{sourceLayerKey}:{style.Name}",
                    Message = isSld
                        ? $"Style '{style.Name}' for layer '{sourceLayerKey}' requires manual SLD conversion before it can be applied in Honua."
                        : $"Style '{style.Name}' for layer '{sourceLayerKey}' uses unsupported format '{style.Format}' and requires manual conversion before it can be applied in Honua.",
                    ManualActions =
                    [
                        isSld
                            ? $"Convert style '{style.Name}' from SLD to a Honua-supported MapLibre payload."
                            : $"Convert style '{style.Name}' from format '{style.Format}' to a Honua-supported MapLibre payload.",
                        $"Apply the converted style after the target layer has been published."
                    ]
                });
            }
        }

        return entries
            .OrderBy(static entry => entry.SourceLayerKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.SourceStyleName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AppendUnsupportedCoverageDiagnostics(
        IReadOnlyList<GeoServerCoverageStoreInfo> coverageStores,
        List<MigrationDiagnostic> diagnostics)
    {
        foreach (var coverageStore in coverageStores)
        {
            diagnostics.Add(new MigrationDiagnostic
            {
                Severity = MigrationDiagnosticSeverity.Warning,
                Code = MigrationReasonCodes.UnsupportedCoverageStore,
                SourceResourceType = "CoverageStore",
                SourceKey = GeoServerSelectionPlanner.GetQualifiedKey(coverageStore.WorkspaceName, coverageStore.Name),
                Message = $"Coverage store '{coverageStore.WorkspaceName}/{coverageStore.Name}' is outside the initial vector translation scope.",
                ManualActions =
                [
                    $"Plan a raster migration workflow for coverage store '{coverageStore.Name}'."
                ]
            });
        }
    }

    private static void AppendUnsupportedLayerGroupDiagnostics(
        IReadOnlyList<GeoServerLayerGroupInfo> layerGroups,
        List<MigrationDiagnostic> diagnostics)
    {
        foreach (var layerGroup in layerGroups)
        {
            diagnostics.Add(new MigrationDiagnostic
            {
                Severity = MigrationDiagnosticSeverity.Warning,
                Code = MigrationReasonCodes.UnsupportedLayerGroup,
                SourceResourceType = "LayerGroup",
                SourceKey = GeoServerSelectionPlanner.GetQualifiedKey(layerGroup.WorkspaceName, layerGroup.Name),
                Message = $"Layer group '{layerGroup.Name}' requires manual recreation after the target services and layers have been published.",
                ManualActions =
                [
                    $"Recreate layer group '{layerGroup.Name}' manually in the target environment."
                ]
            });
        }
    }

    private static GeoServerMigrationSourceSummary CreateSourceSummary(
        string geoServerRestUrl,
        GeoServerServiceInfo serviceInfo,
        GeoServerSelectedResources selected)
    {
        var host = Uri.TryCreate(geoServerRestUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;

        var fingerprint = ComputeSourceFingerprint(serviceInfo, selected);
        var compatibility = serviceInfo.CompatibilityAssessment;

        return new GeoServerMigrationSourceSummary
        {
            GeoServerRestUrl = geoServerRestUrl,
            Host = host,
            GeoServerVersion = serviceInfo.Version,
            SourceFingerprint = fingerprint,
            WorkspaceCount = serviceInfo.Workspaces.Length,
            DataStoreCount = serviceInfo.DataStores.Length,
            CoverageStoreCount = serviceInfo.CoverageStores.Length,
            LayerCount = serviceInfo.Layers.Length,
            LayerGroupCount = serviceInfo.LayerGroups.Length,
            StyleCount = serviceInfo.Styles.Length,
            Compatibility = new MigrationSourceCompatibilitySummary
            {
                FullyCompatibleResources = compatibility?.FullyCompatibleResources ?? 0,
                PartiallyCompatibleResources = compatibility?.PartiallyCompatibleResources ?? 0,
                IncompatibleResources = compatibility?.IncompatibleResources ?? 0,
                CompatibilityPercentage = compatibility?.OverallCompatibilityPercentage ?? 0
            }
        };
    }

    private static MetadataResource CreateServiceMetadataResource(
        string targetServiceName,
        MigrationPublishPlanEntry representativeLayer,
        GeoServerWorkspaceInfo? workspace,
        int srid)
    {
        var spec = CreateJsonObject(
            ("description", $"GeoServer workspace '{representativeLayer.SourceWorkspace}' translated service"),
            ("srid", srid),
            ("sourceType", "GeoServer"),
            ("sourceWorkspace", representativeLayer.SourceWorkspace),
            ("targetServiceName", targetServiceName),
            ("workspaceTitle", workspace?.Description));

        return new MetadataResource
        {
            ApiVersion = MetadataSchemaVersion,
            Kind = MetadataResourceKinds.Service,
            Metadata = new ResourceMetadata
            {
                Name = targetServiceName,
                Namespace = DefaultMetadataNamespace
            },
            Spec = spec
        };
    }

    private static MetadataResource CreateLayerMetadataResource(MigrationPublishPlanEntry plan)
    {
        var spec = CreateJsonObject(
            ("tableName", plan.SourceTableName),
            ("schemaName", plan.SourceSchemaName),
            ("geometryType", plan.GeometryType),
            ("srid", plan.SourceSrid),
            ("sourceType", "GeoServer"),
            ("sourceWorkspace", plan.SourceWorkspace),
            ("sourceLayer", plan.SourceLayerName),
            ("connectionAlias", plan.ConnectionAlias),
            ("serviceName", plan.TargetServiceName),
            ("geometryColumn", plan.GeometryColumn));

        return new MetadataResource
        {
            ApiVersion = MetadataSchemaVersion,
            Kind = MetadataResourceKinds.Layer,
            Metadata = new ResourceMetadata
            {
                Name = plan.TargetLayerName,
                Namespace = plan.TargetServiceName
            },
            Spec = spec
        };
    }

    private static JsonElement CreateJsonObject(params (string Name, object? Value)[] properties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var (name, value) in properties)
        {
            if (value == null)
            {
                continue;
            }

            switch (value)
            {
                case string stringValue:
                    writer.WriteString(name, stringValue);
                    break;
                case int intValue:
                    writer.WriteNumber(name, intValue);
                    break;
                case bool boolValue:
                    writer.WriteBoolean(name, boolValue);
                    break;
                default:
                    writer.WriteString(name, value.ToString());
                    break;
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static string ExtractSourceTableName(GeoServerLayerInfo layer)
        => string.IsNullOrWhiteSpace(layer.NativeName)
            ? layer.Name
            : layer.NativeName!.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();

    private static string? ExtractSourceSchemaName(
        GeoServerLayerInfo layer,
        IReadOnlyList<GeoServerDataStoreInfo> dataStores)
    {
        var nativeName = layer.NativeName;
        if (!string.IsNullOrWhiteSpace(nativeName) && nativeName.Contains('.', StringComparison.Ordinal))
        {
            return nativeName.Split('.', 2, StringSplitOptions.TrimEntries)[0];
        }

        if (string.IsNullOrWhiteSpace(layer.DataStoreName))
        {
            return null;
        }

        var dataStore = dataStores.FirstOrDefault(store =>
            store.WorkspaceName.Equals(layer.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
            store.Name.Equals(layer.DataStoreName, StringComparison.OrdinalIgnoreCase));

        return dataStore == null
            ? null
            : GetOptionalStringParameter(dataStore.ConnectionParameters, "schema");
    }

    private static int? ParseSrid(string? srs)
    {
        if (string.IsNullOrWhiteSpace(srs))
        {
            return null;
        }

        var token = srs.Trim();
        if (token.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            token = token["EPSG:".Length..];
        }

        return int.TryParse(token, out var srid) && srid >= 0 ? srid : null;
    }

    private static string MapTargetServiceName(string workspaceName, GeoServerImportOptions? options)
    {
        if (options?.WorkspaceNameMappings != null &&
            options.WorkspaceNameMappings.TryGetValue(workspaceName, out var mappedName) &&
            !string.IsNullOrWhiteSpace(mappedName))
        {
            return mappedName.Trim();
        }

        return string.IsNullOrWhiteSpace(workspaceName)
            ? options?.DefaultWorkspaceName ?? "geoserver-import"
            : workspaceName;
    }

    private static bool IsSupportedPostGisDataStore(GeoServerDataStoreInfo dataStore)
        => dataStore.Type.Equals("PostGIS", StringComparison.OrdinalIgnoreCase) ||
           dataStore.Type.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);

    private static string? GetRequiredStringParameter(IReadOnlyDictionary<string, object> parameters, string key)
        => parameters.TryGetValue(key, out var value) && value is string stringValue && !string.IsNullOrWhiteSpace(stringValue)
            ? stringValue
            : null;

    private static string? GetOptionalStringParameter(IReadOnlyDictionary<string, object> parameters, string key)
        => parameters.TryGetValue(key, out var value) && value is string stringValue && !string.IsNullOrWhiteSpace(stringValue)
            ? stringValue
            : null;

    private static int? GetOptionalIntParameter(IReadOnlyDictionary<string, object> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue <= int.MaxValue && longValue >= int.MinValue => (int)longValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? NormalizeSslMode(IReadOnlyDictionary<string, object> parameters)
    {
        var sslMode = GetOptionalStringParameter(parameters, "ssl mode")
                      ?? GetOptionalStringParameter(parameters, "sslMode");
        return string.IsNullOrWhiteSpace(sslMode) ? null : sslMode.Trim();
    }

    private static IEnumerable<string> GetOrderedStyleReferences(GeoServerLayerInfo layer)
    {
        var references = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(layer.DefaultStyle))
        {
            references.Add(layer.DefaultStyle);
        }

        foreach (var alternativeStyle in layer.AlternativeStyles)
        {
            if (!string.IsNullOrWhiteSpace(alternativeStyle))
            {
                references.Add(alternativeStyle);
            }
        }

        return references;
    }

    private static bool TryResolveStyle(
        string reference,
        string layerWorkspace,
        IReadOnlyDictionary<string, GeoServerStyleInfo> styles,
        out GeoServerStyleInfo style)
    {
        if (styles.TryGetValue(reference, out style!))
        {
            return true;
        }

        var qualifiedReference = GeoServerSelectionPlanner.GetQualifiedKey(layerWorkspace, reference);
        return styles.TryGetValue(qualifiedReference, out style!);
    }

    private static string BuildStyleReferenceUrl(string baseUrl, GeoServerStyleInfo style)
    {
        var trimmedBaseUrl = baseUrl.TrimEnd('/');
        var extension = GetStyleReferenceExtension(style);
        return string.IsNullOrWhiteSpace(style.WorkspaceName)
            ? $"{trimmedBaseUrl}/styles/{Uri.EscapeDataString(style.Name)}.{extension}"
            : $"{trimmedBaseUrl}/workspaces/{Uri.EscapeDataString(style.WorkspaceName)}/styles/{Uri.EscapeDataString(style.Name)}.{extension}";
    }

    private static string GetStyleReferenceExtension(GeoServerStyleInfo style)
    {
        if (!string.IsNullOrWhiteSpace(style.Filename))
        {
            var filenameExtension = Path.GetExtension(style.Filename.Trim());
            if (!string.IsNullOrWhiteSpace(filenameExtension))
            {
                return filenameExtension.TrimStart('.').ToLowerInvariant();
            }
        }

        if (!string.IsNullOrWhiteSpace(style.Format))
        {
            return style.Format.Trim().TrimStart('.').ToLowerInvariant();
        }

        return "sld";
    }

    private static int CountManualActions(
        IReadOnlyList<MigrationConnectionDraft> connectionDrafts,
        IReadOnlyList<MigrationPublishPlanEntry> publishPlan,
        IReadOnlyList<MigrationStylePlanEntry> stylePlan,
        IReadOnlyList<MigrationDiagnostic> diagnostics)
    {
        var manualConnections = connectionDrafts.Count(static draft => draft.Status == MigrationPlanStatus.ManualActionRequired);
        var manualPublishPlans = publishPlan.Count(static entry => entry.Status == MigrationPlanStatus.ManualActionRequired);
        var manualStyles = stylePlan.Count(static entry => entry.TranslationStatus == MigrationStyleTranslationStatus.ManualActionRequired);
        var manualDiagnostics = diagnostics.Count(static diagnostic => diagnostic.ManualActions.Count > 0);
        return manualConnections + manualPublishPlans + manualStyles + manualDiagnostics;
    }

    private static int CountUnsupported(
        IReadOnlyList<MigrationPublishPlanEntry> publishPlan,
        IReadOnlyList<MigrationStylePlanEntry> stylePlan,
        IReadOnlyList<MigrationDiagnostic> diagnostics)
    {
        var unsupportedPublishPlans = publishPlan.Count(static entry => entry.Status == MigrationPlanStatus.Unsupported);
        var unsupportedStyles = stylePlan.Count(static entry => entry.TranslationStatus == MigrationStyleTranslationStatus.Unsupported);
        var unsupportedDiagnostics = diagnostics.Count(static diagnostic =>
            diagnostic.Code.StartsWith("unsupported-", StringComparison.OrdinalIgnoreCase));
        return unsupportedPublishPlans + unsupportedStyles + unsupportedDiagnostics;
    }

    private static string ComputeSourceFingerprint(GeoServerServiceInfo serviceInfo, GeoServerSelectedResources selected)
    {
        var builder = new StringBuilder();
        builder.Append(serviceInfo.GeoServerRestUrl).Append('|')
            .Append(serviceInfo.Version).Append('|');

        foreach (var workspace in selected.Workspaces)
        {
            builder.Append("ws:").Append(workspace.Name).Append('|');
        }

        foreach (var dataStore in selected.DataStores)
        {
            builder.Append("ds:")
                .Append(dataStore.WorkspaceName)
                .Append(':')
                .Append(dataStore.Name)
                .Append(':')
                .Append(dataStore.Type)
                .Append('|');
        }

        foreach (var layer in selected.Layers)
        {
            builder.Append("lyr:")
                .Append(layer.WorkspaceName)
                .Append(':')
                .Append(layer.Name)
                .Append(':')
                .Append(layer.DataStoreName ?? layer.CoverageStoreName)
                .Append(':')
                .Append(layer.NativeName)
                .Append(':')
                .Append(layer.SRS)
                .Append(':')
                .Append(layer.GeometryType)
                .Append('|');
        }

        foreach (var style in selected.Styles)
        {
            builder.Append("sty:")
                .Append(style.WorkspaceName)
                .Append(':')
                .Append(style.Name)
                .Append(':')
                .Append(style.Format)
                .Append('|');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string GetTranslatorVersion()
    {
        var assembly = typeof(GeoServerMigrationManifestService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        var separator = informationalVersion.IndexOf('+');
        return separator >= 0 ? informationalVersion[..separator] : informationalVersion;
    }

    private const string MetadataSchemaVersion = "honua.io/v1alpha1";

    private static partial class Log
    {
        [LoggerMessage(8012, LogLevel.Information, "Starting GeoServer translation for {GeoServerUrl}")]
        public static partial void TranslationStarting(ILogger logger, string geoServerUrl);

        [LoggerMessage(8013, LogLevel.Information, "GeoServer translation completed in {Duration}. Drafts={ConnectionDraftCount}, PublishPlans={PublishPlanCount}, StylePlans={StylePlanCount}, Diagnostics={DiagnosticCount}, ManualActions={ManualActionCount}, Unsupported={UnsupportedCount}")]
        public static partial void TranslationCompleted(
            ILogger logger,
            TimeSpan duration,
            int connectionDraftCount,
            int publishPlanCount,
            int stylePlanCount,
            int diagnosticCount,
            int manualActionCount,
            int unsupportedCount);

        [LoggerMessage(8014, LogLevel.Error, "GeoServer translation failed for {GeoServerUrl}")]
        public static partial void TranslationFailed(ILogger logger, string geoServerUrl, Exception exception);
    }
}
