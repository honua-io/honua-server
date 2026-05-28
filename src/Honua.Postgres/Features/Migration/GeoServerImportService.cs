// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Styling.Abstractions;
using Microsoft.Extensions.Logging;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Service for importing GeoServer configuration into Honua.
/// </summary>
internal sealed partial class GeoServerImportService : IGeoServerImportService
{
    private const int PostgresIdentifierMaxLength = 63;
    private const int FeatureCopyTargetHashByteLength = 4;

    private readonly GeoServerRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ISldStyleConverter? _sldConverter;
    private readonly ILayerPublishingService? _layerPublishingService;
    private readonly IMigrationCatalogWriter? _catalogWriter;
    private readonly ILogger<GeoServerImportService> _logger;

    public GeoServerImportService(
        GeoServerRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        ILogger<GeoServerImportService> logger,
        ISldStyleConverter? sldConverter = null,
        ILayerPublishingService? layerPublishingService = null,
        IMigrationCatalogWriter? catalogWriter = null)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sldConverter = sldConverter;
        _layerPublishingService = layerPublishingService;
        _catalogWriter = catalogWriter;
    }

    /// <inheritdoc />
    public Task<GeoServerServiceInfo> DiscoverServiceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _restClient.DiscoverServiceAsync(
            request.GeoServerRestUrl,
            request.Username,
            request.Password,
            request.IncludeCompatibilityAnalysis,
            request.IncludeStyleContent,
            request.TimeoutSeconds,
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            request.AllowUnsafeLocalUrls,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metricsRecorder = new MigrationRunMetricsRecorder();
        metricsRecorder.SampleResources();

        try
        {
            GeoServerServiceInfo serviceInfo;
            MigrationSourceInventoryArtifact inventory;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Scan))
            {
                serviceInfo = await DiscoverServiceAsync(request, cancellationToken).ConfigureAwait(false);
                metricsRecorder.RecordSourceRequest();
                inventory = await BuildInventoryArtifactAsync(
                        serviceInfo,
                        request.Username,
                        request.Password,
                        request.IncludeStyleContent,
                        cancellationToken)
                    .ConfigureAwait(false);
                metricsRecorder.RecordResourceCount(inventory.Summary.ResourceCount);
                metricsRecorder.SampleResources();
            }

            var metrics = BuildRunMetricsArtifact(metricsRecorder, serviceInfo, runId: null, measurementScope: "geoserver scan");
            EmitRunMetrics(metrics);
            return inventory;
        }
        catch (InvalidOperationException ex)
        {
            Log.InventoryScanFailed(_logger, request.GeoServerRestUrl, ex);

            return new MigrationSourceInventoryArtifact
            {
                SourceKind = "geoserver-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = "GeoServer",
                    BaseUrl = request.GeoServerRestUrl,
                    Product = "GeoServer",
                    ServiceType = "REST"
                },
                AuthPosture = BuildAuthPosture(
                    request.Username,
                    request.Password,
                    accessConfirmed: false,
                    anonymousMode: "anonymous-or-auth-required",
                    notes: [ex.Message]),
                ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness(
                    "failed",
                    [ex.Message],
                    ["source-inventory"]),
                Summary = new MigrationInventorySummary(),
                OverallCompatibility = MigrationInventoryHelpers.Partial(
                    "The scan did not complete successfully.",
                    [ex.Message],
                    ["Verify GeoServer reachability and credentials, then rerun the scan."],
                    ImportCompatibilityCodes.GeoServerScanFailed),
                Containers = [],
                Resources = [],
                Styles = [],
                ExternalDependencies = []
            };
        }
    }

    /// <inheritdoc />
    public Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportConfigurationAsync(request, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        IProgress<GeoServerImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Log.ImportStarting(_logger, request.GeoServerRestUrl, request.TargetHonuaUrl);

        var stopwatch = Stopwatch.StartNew();
        var jobId = request.JobId ?? Guid.NewGuid().ToString();

        using var activity = GeoServerImportActivity.StartImport(request.GeoServerRestUrl, request.TargetHonuaUrl);

        var metricsRecorder = new MigrationRunMetricsRecorder();
        metricsRecorder.SampleResources();

        // Initialize progress tracking
        var currentProgress = GeoServerImportProgress.CreateInitial(
            jobId,
            request.GeoServerRestUrl,
            request.TargetHonuaUrl);

        try
        {
            progress?.Report(currentProgress);

            // Step 1: Discover GeoServer configuration
            Log.DiscoveringConfiguration(_logger, request.GeoServerRestUrl);
            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Discovering,
                CurrentPhase = "Discovering GeoServer configuration"
            };
            progress?.Report(currentProgress);

            var discoveryRequest = new GeoServerDiscoveryRequest
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Username = request.Username,
                Password = request.Password,
                TimeoutSeconds = request.RequestTimeoutSeconds,
                IncludeCompatibilityAnalysis = true,
                IncludeStyleContent = request.ImportStyles,
                AllowUnsafeLocalUrls = request.AllowUnsafeLocalUrls
            };

            GeoServerServiceInfo serviceInfo;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Scan))
            {
                serviceInfo = await DiscoverServiceAsync(discoveryRequest, cancellationToken);
                metricsRecorder.RecordSourceRequest();
                metricsRecorder.SampleResources();
            }

            // Filter resources based on request
            var filteredResources = FilterRequestedResources(serviceInfo, request);
            metricsRecorder.RecordResourceCount(
                filteredResources.WorkspaceCount +
                filteredResources.DataStoreCount +
                filteredResources.LayerCount +
                filteredResources.StyleCount);

            // Estimate total work
            var totalResources = filteredResources.WorkspaceCount + filteredResources.DataStoreCount +
                                filteredResources.LayerCount + (request.ImportStyles ? filteredResources.StyleCount : 0);

            currentProgress = currentProgress with
            {
                EstimatedTotalResources = totalResources,
                SourceGeoServerVersion = serviceInfo.Version,
                CurrentPhase = $"Discovered {totalResources} resources to import"
            };
            progress?.Report(currentProgress);

            if (request.DryRun)
            {
                currentProgress = currentProgress with
                {
                    Status = GeoServerImportStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Dry run completed"
                };
                metricsRecorder.SampleResources();
                var dryRunMetrics = BuildRunMetricsArtifact(metricsRecorder, serviceInfo, jobId, "geoserver dry run");
                EmitRunMetrics(dryRunMetrics);
                currentProgress = currentProgress with { RunMetrics = dryRunMetrics };
                progress?.Report(currentProgress);

                return CreateDryRunResult(serviceInfo, filteredResources, request, stopwatch.Elapsed) with
                {
                    RunMetrics = dryRunMetrics
                };
            }

            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Validating,
                CurrentPhase = "Generating deterministic apply plan"
            };
            progress?.Report(currentProgress);

            MigrationApplyPlanArtifact applyPlan;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Manifest))
            {
                applyPlan = await CreateApplyPlanAsync(serviceInfo, filteredResources, request, cancellationToken)
                    .ConfigureAwait(false);
                metricsRecorder.RecordResourceCount(applyPlan.Summary.TotalStepCount);
                metricsRecorder.RecordManualReview(applyPlan.Summary.ManualReviewStepCount, applyPlan.Summary.TotalStepCount);
                metricsRecorder.SampleResources();
            }

            MigrationApplyExecutionArtifact applyExecution;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Apply))
            {
                applyExecution = await ExecuteApplyPlanAsync(
                        filteredResources,
                        request,
                        applyPlan,
                        currentProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                metricsRecorder.RecordResourceCount(applyExecution.Summary.AppliedStepCount);
                metricsRecorder.RecordResume(applyPlan.ReplayToken, applyExecution.Summary.AlreadyAppliedStepCount);
                metricsRecorder.SampleResources();
            }

            var applyPlanWarnings = BuildApplyPlanWarnings(applyPlan, applyExecution);

            var runMetrics = BuildRunMetricsArtifact(metricsRecorder, serviceInfo, jobId, "geoserver migration");
            EmitRunMetrics(runMetrics);

            if (applyExecution.Summary.FailedStepCount > 0)
            {
                var failureMessage = BuildApplyFailureMessage(applyExecution);
                currentProgress = currentProgress with
                {
                    Status = GeoServerImportStatus.Failed,
                    ResourcesProcessed = applyPlan.Summary.TotalStepCount,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Apply plan failed",
                    ErrorMessage = failureMessage,
                    Warnings = applyPlanWarnings,
                    ApplyPlan = applyPlan,
                    ApplyExecution = applyExecution,
                    RunMetrics = runMetrics
                };
                progress?.Report(currentProgress);

                return CreateFailedApplyPlanResult(
                    serviceInfo,
                    applyPlan,
                    applyExecution,
                    request,
                    stopwatch.Elapsed,
                    applyPlanWarnings,
                    failureMessage) with
                { RunMetrics = runMetrics };
            }

            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Completed,
                ResourcesProcessed = applyPlan.Summary.TotalStepCount,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Apply plan executed",
                Warnings = applyPlanWarnings,
                ApplyPlan = applyPlan,
                ApplyExecution = applyExecution,
                RunMetrics = runMetrics
            };
            progress?.Report(currentProgress);

            Log.ApplyPlanExecuted(
                _logger,
                applyPlan.Summary.TotalStepCount,
                applyExecution.Summary.AppliedStepCount,
                applyExecution.Summary.AlreadyAppliedStepCount,
                applyExecution.Summary.ManualReviewStepCount,
                applyExecution.Summary.UnsupportedStepCount);

            return CreateApplyPlanResult(serviceInfo, applyPlan, applyExecution, request, stopwatch.Elapsed, applyPlanWarnings) with
            {
                RunMetrics = runMetrics
            };
        }
        catch (OperationCanceledException)
        {
            Log.ImportCancelled(_logger);
            throw;
        }
        catch (Exception ex)
        {
            Log.ImportFailed(_logger, ex.Message, ex);

            var errorProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import failed",
                ErrorMessage = ex.Message
            };
            progress?.Report(errorProgress);

            return GeoServerImportResult.CreateFailure(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                ex.Message,
                stopwatch.Elapsed);
        }
    }

    private static FilteredResources FilterRequestedResources(GeoServerServiceInfo serviceInfo, GeoServerImportRequest request)
    {
        var styleIdsByReference = BuildStyleReferenceLookup(serviceInfo.Styles);

        var dataStores = request.DataStoreNames == null
            ? serviceInfo.DataStores
            : serviceInfo.DataStores.Where(ds => IsResourceRequested(ds.WorkspaceName, ds.Name, request.DataStoreNames)).ToArray();

        var layers = request.LayerNames == null
            ? serviceInfo.Layers
            : serviceInfo.Layers.Where(l => IsResourceRequested(l.WorkspaceName, l.Name, request.LayerNames)).ToArray();

        var styles = request.ImportStyles
            ? (request.LayerNames == null ? serviceInfo.Styles : serviceInfo.Styles.Where(s => IsResourceNeededForLayers(s, layers, styleIdsByReference)).ToArray())
            : Array.Empty<GeoServerStyleInfo>();

        var layerGroups = request.LayerNames == null
            ? serviceInfo.LayerGroups
                .Where(group => request.WorkspaceNames == null ||
                    request.WorkspaceNames.Contains(group.WorkspaceName, StringComparer.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<GeoServerLayerGroupInfo>();

        // Scope workspaces to the operator's request. When WorkspaceNames is set we honor
        // it directly; otherwise, when LayerNames narrows the scope we derive the
        // workspace set from the selected layers (and any layer groups) so the
        // catalog-apply loop does not persist honua.services rows for workspaces the
        // operator never asked to touch (issue #1098). When neither filter is set we
        // preserve the historical "all workspaces" behavior.
        GeoServerWorkspaceInfo[] workspaces;
        if (request.WorkspaceNames != null)
        {
            workspaces = serviceInfo.Workspaces
                .Where(w => request.WorkspaceNames.Contains(w.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        else if (request.LayerNames != null)
        {
            var scopedWorkspaceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in layers)
            {
                if (!string.IsNullOrWhiteSpace(layer.WorkspaceName))
                {
                    scopedWorkspaceNames.Add(layer.WorkspaceName);
                }
            }
            foreach (var group in layerGroups)
            {
                if (!string.IsNullOrWhiteSpace(group.WorkspaceName))
                {
                    scopedWorkspaceNames.Add(group.WorkspaceName);
                }
            }
            workspaces = serviceInfo.Workspaces
                .Where(w => scopedWorkspaceNames.Contains(w.Name))
                .ToArray();
        }
        else
        {
            workspaces = serviceInfo.Workspaces;
        }

        var scopedWorkspaceNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in workspaces)
        {
            if (!string.IsNullOrWhiteSpace(workspace.Name))
            {
                scopedWorkspaceNameSet.Add(workspace.Name);
            }
        }

        return new FilteredResources
        {
            Workspaces = workspaces,
            DataStores = dataStores,
            Layers = layers,
            LayerGroups = layerGroups,
            Styles = styles,
            WorkspaceCount = workspaces.Length,
            DataStoreCount = dataStores.Length,
            LayerCount = layers.Length,
            StyleCount = styles.Length,
            ScopedWorkspaceNames = scopedWorkspaceNameSet
        };
    }

    private static bool IsResourceRequested(string workspaceName, string resourceName, string[] requestedNames)
    {
        return requestedNames.Any(name =>
            name.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals($"{workspaceName}:{resourceName}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsResourceNeededForLayers(
        GeoServerStyleInfo style,
        GeoServerLayerInfo[] layers,
        IReadOnlyDictionary<StyleReferenceKey, string[]> styleIdsByReference)
    {
        var styleId = GetStyleId(style);
        return layers.Any(layer =>
            ResolveStyleIds(styleIdsByReference, layer.WorkspaceName, layer.DefaultStyle).Contains(styleId, StringComparer.Ordinal) ||
            layer.AlternativeStyles.Any(alternativeStyle => ResolveStyleIds(styleIdsByReference, layer.WorkspaceName, alternativeStyle)
                .Contains(styleId, StringComparer.Ordinal)));
    }

    private static MigrationInventoryAuthPosture BuildAuthPosture(
        string? username,
        string? password,
        bool accessConfirmed,
        string anonymousMode,
        IEnumerable<string>? notes = null)
    {
        var usesBasicAuth = UsesBasicAuthentication(username, password);
        var postureNotes = new List<string>();

        if (!usesBasicAuth && HasAnyCredential(username, password))
        {
            postureNotes.Add("GeoServer basic authentication requires both username and password; the scan proceeded without credentials.");
        }

        if (notes != null)
        {
            postureNotes.AddRange(notes);
        }

        return new MigrationInventoryAuthPosture
        {
            Mode = usesBasicAuth ? "basic" : anonymousMode,
            CredentialsSupplied = usesBasicAuth,
            AccessConfirmed = accessConfirmed,
            Notes = MigrationInventoryHelpers.NormalizeStrings(postureNotes)
        };
    }

    private static bool UsesBasicAuthentication(string? username, string? password)
        => !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);

    private static bool HasAnyCredential(string? username, string? password)
        => !string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password);

    private static Dictionary<StyleReferenceKey, string[]> BuildStyleReferenceLookup(IEnumerable<GeoServerStyleInfo> styles)
    {
        return styles
            .GroupBy(
                static style => new StyleReferenceKey(style.WorkspaceName, style.Name),
                StyleReferenceKey.Comparer)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(GetStyleId).ToArray(),
                StyleReferenceKey.Comparer);
    }

    private static string[] ResolveStyleIds(
        IReadOnlyDictionary<StyleReferenceKey, string[]> styleIdsByReference,
        string layerWorkspaceName,
        string? styleReference)
    {
        if (string.IsNullOrWhiteSpace(styleReference))
        {
            return [];
        }

        if (TryParseQualifiedStyleReference(styleReference, out var qualifiedReference))
        {
            return styleIdsByReference.TryGetValue(qualifiedReference, out var qualifiedStyleIds)
                ? qualifiedStyleIds
                : [];
        }

        var workspaceReference = new StyleReferenceKey(layerWorkspaceName, styleReference);
        if (styleIdsByReference.TryGetValue(workspaceReference, out var workspaceStyleIds))
        {
            return workspaceStyleIds;
        }

        var globalReference = new StyleReferenceKey(null, styleReference);
        return styleIdsByReference.TryGetValue(globalReference, out var globalStyleIds)
            ? globalStyleIds
            : [];
    }

    private static bool TryParseQualifiedStyleReference(string styleReference, out StyleReferenceKey qualifiedReference)
    {
        var separatorIndex = styleReference.IndexOf(':');
        if (separatorIndex > 0 && separatorIndex < styleReference.Length - 1)
        {
            qualifiedReference = new StyleReferenceKey(styleReference[..separatorIndex], styleReference[(separatorIndex + 1)..]);
            return true;
        }

        qualifiedReference = default;
        return false;
    }

    private readonly record struct StyleReferenceKey(string? WorkspaceName, string StyleName)
    {
        public static IEqualityComparer<StyleReferenceKey> Comparer { get; } = new StyleReferenceKeyComparer();

        private sealed class StyleReferenceKeyComparer : IEqualityComparer<StyleReferenceKey>
        {
            public bool Equals(StyleReferenceKey x, StyleReferenceKey y)
                => string.Equals(x.WorkspaceName, y.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.StyleName, y.StyleName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(StyleReferenceKey obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WorkspaceName ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.StyleName));
        }
    }

    private GeoServerImportResult CreateDryRunResult(GeoServerServiceInfo serviceInfo, FilteredResources resources, GeoServerImportRequest request, TimeSpan duration)
    {
        var importedResources = new List<GeoServerImportedResource>();

        // Add what would be imported
        foreach (var workspace in resources.Workspaces)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "Workspace",
                Name = workspace.Name,
                Notes = "Would be created"
            });
        }

        foreach (var dataStore in resources.DataStores)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "DataStore",
                Name = dataStore.Name,
                WorkspaceName = dataStore.WorkspaceName,
                Notes = $"Would be created (type: {dataStore.Type})"
            });
        }

        foreach (var layer in resources.Layers)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "Layer",
                Name = layer.Name,
                WorkspaceName = layer.WorkspaceName,
                Notes = "Would be created"
            });
        }

        foreach (var style in resources.Styles)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "Style",
                Name = style.Name,
                WorkspaceName = style.WorkspaceName,
                Notes = style.Format == "sld"
                    ? (_sldConverter != null
                        ? "Would convert SLD to MapLibre via ISldStyleConverter"
                        : "SLD converter not registered; style would be skipped or warned (issue #375)")
                    : "Would be created"
            });
        }

        var warnings = new List<string>();
        if (resources.StyleCount > 0 && _sldConverter == null)
        {
            warnings.Add("ISldStyleConverter is not registered; SLD styles will be skipped or warned at import time.");
        }

        if (serviceInfo.CompatibilityAssessment?.IncompatibleResources > 0)
        {
            warnings.Add($"{serviceInfo.CompatibilityAssessment.IncompatibleResources} resources are incompatible and would need manual intervention");
        }

        return GeoServerImportResult.CreateSuccess(
            request.GeoServerRestUrl,
            request.TargetHonuaUrl,
            resources.WorkspaceCount,
            resources.DataStoreCount,
            resources.LayerCount,
            resources.StyleCount,
            serviceInfo.Version,
            duration,
            warnings,
            importedResources,
            wasDryRun: true);
    }



    // Helper classes for internal state tracking
    private sealed record FilteredResources
    {
        public GeoServerWorkspaceInfo[] Workspaces { get; init; } = [];
        public GeoServerDataStoreInfo[] DataStores { get; init; } = [];
        public GeoServerLayerInfo[] Layers { get; init; } = [];
        public GeoServerLayerGroupInfo[] LayerGroups { get; init; } = [];
        public GeoServerStyleInfo[] Styles { get; init; } = [];
        public int WorkspaceCount { get; init; }
        public int DataStoreCount { get; init; }
        public int LayerCount { get; init; }
        public int StyleCount { get; init; }

        /// <summary>
        /// Defensive scoping set used by catalog-apply write sites to reject any
        /// resource whose source workspace falls outside the operator's requested
        /// scope (issue #1098). The set is derived from the filtered workspace
        /// list so the apply path stays self-consistent even if a future plan
        /// builder forwards a step whose workspace was excluded by the filter.
        /// </summary>
        public HashSet<string> ScopedWorkspaceNames { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record ImportStepResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int SkippedCount { get; set; }
        public List<GeoServerImportedResource> ImportedResources { get; init; } = new();
        public List<GeoServerFailedResource> FailedResources { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public GeoServerImportResourceBreakdown? ResourceBreakdown { get; set; }
    }

    private static partial class Log
    {
        [LoggerMessage(7990, LogLevel.Information, "Starting GeoServer import from {SourceUrl} to {TargetUrl}")]
        public static partial void ImportStarting(ILogger logger, string sourceUrl, string targetUrl);

        [LoggerMessage(7991, LogLevel.Information, "Discovering GeoServer configuration at {GeoServerUrl}")]
        public static partial void DiscoveringConfiguration(ILogger logger, string geoServerUrl);

        [LoggerMessage(7992, LogLevel.Information, "GeoServer import completed successfully in {Duration}. Imported: {WorkspaceCount} workspaces, {DataStoreCount} datastores, {LayerCount} layers, {StyleCount} styles")]
        public static partial void ImportCompleted(
            ILogger logger,
            TimeSpan duration,
            int workspaceCount,
            int dataStoreCount,
            int layerCount,
            int styleCount);

        [LoggerMessage(7993, LogLevel.Warning, "GeoServer import was cancelled")]
        public static partial void ImportCancelled(ILogger logger);

        [LoggerMessage(7994, LogLevel.Error, "GeoServer import failed: {ErrorMessage}")]
        public static partial void ImportFailed(ILogger logger, string errorMessage, Exception exception);

        [LoggerMessage(79945, LogLevel.Warning, "GeoServer inventory scan failed for {GeoServerUrl}")]
        public static partial void InventoryScanFailed(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(79946, LogLevel.Information, "GeoServer apply plan executed with {StepCount} steps, {AppliedStepCount} applied steps, {AlreadyAppliedStepCount} idempotent replays, {ManualReviewStepCount} manual-review steps, and {UnsupportedStepCount} unsupported steps")]
        public static partial void ApplyPlanExecuted(
            ILogger logger,
            int stepCount,
            int appliedStepCount,
            int alreadyAppliedStepCount,
            int manualReviewStepCount,
            int unsupportedStepCount);

        [LoggerMessage(7995, LogLevel.Information, "Importing {Count} workspaces")]
        public static partial void ImportingWorkspaces(ILogger logger, int count);

        [LoggerMessage(7996, LogLevel.Information, "Would create workspace: {WorkspaceName}")]
        public static partial void WorkspaceImported(ILogger logger, string workspaceName);

        [LoggerMessage(7997, LogLevel.Error, "Failed to import workspace {WorkspaceName}")]
        public static partial void WorkspaceImportFailed(ILogger logger, string workspaceName, Exception exception);

        [LoggerMessage(7998, LogLevel.Information, "Importing {Count} datastores")]
        public static partial void ImportingDataStores(ILogger logger, int count);

        [LoggerMessage(7999, LogLevel.Warning, "Skipping incompatible datastore {DataStoreName}: {Reason}")]
        public static partial void DataStoreSkipped(ILogger logger, string dataStoreName, string reason);

        [LoggerMessage(8000, LogLevel.Information, "Would create datastore: {WorkspaceName}/{DataStoreName} (type: {Type})")]
        public static partial void DataStoreImported(ILogger logger, string workspaceName, string dataStoreName, string type);

        [LoggerMessage(8001, LogLevel.Error, "Failed to import datastore {WorkspaceName}/{DataStoreName}")]
        public static partial void DataStoreImportFailed(ILogger logger, string workspaceName, string dataStoreName, Exception exception);

        [LoggerMessage(8002, LogLevel.Information, "Importing {Count} layers")]
        public static partial void ImportingLayers(ILogger logger, int count);

        [LoggerMessage(8003, LogLevel.Warning, "Skipping incompatible layer {LayerName}: {Reason}")]
        public static partial void LayerSkipped(ILogger logger, string layerName, string reason);

        [LoggerMessage(8004, LogLevel.Information, "Would create layer: {WorkspaceName}/{LayerName}")]
        public static partial void LayerImported(ILogger logger, string workspaceName, string layerName);

        [LoggerMessage(8005, LogLevel.Error, "Failed to import layer {WorkspaceName}/{LayerName}")]
        public static partial void LayerImportFailed(ILogger logger, string workspaceName, string layerName, Exception exception);

        [LoggerMessage(8006, LogLevel.Information, "Importing {Count} styles")]
        public static partial void ImportingStyles(ILogger logger, int count);

        [LoggerMessage(8007, LogLevel.Warning, "Skipping SLD style {StyleName}: requires issue #375")]
        public static partial void StyleSkipped(ILogger logger, string styleName);

        [LoggerMessage(8008, LogLevel.Warning, "SLD style {StyleName} requires conversion to MapLibre format (issue #375)")]
        public static partial void StyleRequiresConversion(ILogger logger, string styleName);

        [LoggerMessage(8009, LogLevel.Information, "Would convert and import style: {WorkspaceName}/{StyleName} (format: {Format})")]
        public static partial void StyleImported(ILogger logger, string workspaceName, string styleName, string format);

        [LoggerMessage(8010, LogLevel.Error, "Failed to import style {WorkspaceName}/{StyleName}")]
        public static partial void StyleImportFailed(ILogger logger, string workspaceName, string styleName, Exception exception);

        [LoggerMessage(8011, LogLevel.Information, "Creating workspace metadata: {WorkspaceName}")]
        public static partial void CreatingWorkspaceMetadata(ILogger logger, string workspaceName);

        [LoggerMessage(8012, LogLevel.Information, "Validating imported resources")]
        public static partial void ValidatingImportedResources(ILogger logger);

        [LoggerMessage(8013, LogLevel.Information, "Import validation completed successfully")]
        public static partial void ImportValidationCompleted(ILogger logger);

        [LoggerMessage(8014, LogLevel.Information, "Created datastore configuration: {WorkspaceName}/{DataStoreName}")]
        public static partial void DataStoreConfigurationCreated(ILogger logger, string workspaceName, string dataStoreName);

        [LoggerMessage(8015, LogLevel.Information, "Created layer configuration: {WorkspaceName}/{LayerName}")]
        public static partial void LayerConfigurationCreated(ILogger logger, string workspaceName, string layerName);

        [LoggerMessage(8016, LogLevel.Information, "Starting style conversion: {StyleName}")]
        public static partial void StyleConversionStarted(ILogger logger, string styleName);

        [LoggerMessage(8017, LogLevel.Information, "Migration run metrics emitted for {SourceKind}/{SourceFamily}: {DurationMs}ms, {RequestCount} source requests, {ResourceCount} resources across {PhaseCount} phases")]
        public static partial void MigrationRunMetricsEmitted(
            ILogger logger,
            string sourceKind,
            string sourceFamily,
            long durationMs,
            long requestCount,
            long resourceCount,
            int phaseCount);

        [LoggerMessage(8018, LogLevel.Warning, "GeoServer catalog-apply rejected cross-workspace write: {EntryKind} owned by workspace '{WorkspaceName}' is outside the operator's requested scope (issue #1098)")]
        public static partial void WorkspaceWriteRejected(ILogger logger, string entryKind, string workspaceName);
    }

}
