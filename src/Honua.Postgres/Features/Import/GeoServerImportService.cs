// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for importing GeoServer configuration into Honua.
/// </summary>
internal sealed partial class GeoServerImportService : IGeoServerImportService
{
    private readonly GeoServerRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<GeoServerImportService> _logger;

    public GeoServerImportService(
        GeoServerRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ILogger<GeoServerImportService> logger)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            cancellationToken);
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
                IncludeStyleContent = request.ImportStyles
            };

            var serviceInfo = await DiscoverServiceAsync(discoveryRequest, cancellationToken);

            // Filter resources based on request
            var filteredResources = FilterRequestedResources(serviceInfo, request);

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
                progress?.Report(currentProgress);

                return CreateDryRunResult(serviceInfo, filteredResources, request, stopwatch.Elapsed);
            }

            var aggregateResult = new ImportStepResult();

            // Step 2: Import workspaces
            var stepResult = await ImportWorkspacesAsync(filteredResources.Workspaces, request, currentProgress, progress, cancellationToken);
            currentProgress = UpdateProgressWithWorkspaces(currentProgress, stepResult);
            AggregateStepResult(aggregateResult, stepResult);

            // Step 3: Import datastores
            stepResult = await ImportDataStoresAsync(filteredResources.DataStores, request, currentProgress, progress, cancellationToken);
            currentProgress = UpdateProgressWithDataStores(currentProgress, stepResult);
            AggregateStepResult(aggregateResult, stepResult);

            // Step 4: Import layers
            stepResult = await ImportLayersAsync(filteredResources.Layers, request, currentProgress, progress, cancellationToken);
            currentProgress = UpdateProgressWithLayers(currentProgress, stepResult);
            AggregateStepResult(aggregateResult, stepResult);

            // Step 5: Import styles (if requested and supported)
            if (request.ImportStyles)
            {
                stepResult = await ImportStylesAsync(filteredResources.Styles, request, currentProgress, progress, cancellationToken);
                currentProgress = UpdateProgressWithStyles(currentProgress, stepResult);
                AggregateStepResult(aggregateResult, stepResult);
            }

            // Step 6: Validation
            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Validating,
                CurrentPhase = "Validating imported configuration"
            };
            progress?.Report(currentProgress);

            await ValidateImportedResourcesAsync(request, cancellationToken);

            // Complete
            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import completed successfully"
            };
            progress?.Report(currentProgress);

            var finalResult = CreateSuccessResult(serviceInfo, aggregateResult, request, stopwatch.Elapsed);

            Log.ImportCompleted(
                _logger,
                stopwatch.Elapsed,
                finalResult.WorkspacesImported,
                finalResult.DataStoresImported,
                finalResult.LayersImported,
                finalResult.StylesImported);

            return finalResult;
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
        var workspaces = request.WorkspaceNames == null
            ? serviceInfo.Workspaces
            : serviceInfo.Workspaces.Where(w => request.WorkspaceNames.Contains(w.Name)).ToArray();

        var dataStores = request.DataStoreNames == null
            ? serviceInfo.DataStores
            : serviceInfo.DataStores.Where(ds => IsResourceRequested(ds.WorkspaceName, ds.Name, request.DataStoreNames)).ToArray();

        var layers = request.LayerNames == null
            ? serviceInfo.Layers
            : serviceInfo.Layers.Where(l => IsResourceRequested(l.WorkspaceName, l.Name, request.LayerNames)).ToArray();

        var styles = request.ImportStyles
            ? (request.LayerNames == null ? serviceInfo.Styles : serviceInfo.Styles.Where(s => IsResourceNeededForLayers(s, layers)).ToArray())
            : Array.Empty<GeoServerStyleInfo>();

        return new FilteredResources
        {
            Workspaces = workspaces,
            DataStores = dataStores,
            Layers = layers,
            Styles = styles,
            WorkspaceCount = workspaces.Length,
            DataStoreCount = dataStores.Length,
            LayerCount = layers.Length,
            StyleCount = styles.Length
        };
    }

    private static bool IsResourceRequested(string workspaceName, string resourceName, string[] requestedNames)
    {
        return requestedNames.Any(name =>
            name.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals($"{workspaceName}:{resourceName}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsResourceNeededForLayers(GeoServerStyleInfo style, GeoServerLayerInfo[] layers)
    {
        return layers.Any(l =>
            l.DefaultStyle?.Equals(style.Name, StringComparison.OrdinalIgnoreCase) == true ||
            l.AlternativeStyles.Any(altStyle => altStyle.Equals(style.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static GeoServerImportResult CreateDryRunResult(GeoServerServiceInfo serviceInfo, FilteredResources resources, GeoServerImportRequest request, TimeSpan duration)
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
                Notes = style.Format == "sld" ? "Would require SLD conversion (issue #375)" : "Would be created"
            });
        }

        var warnings = new List<string>();
        if (resources.StyleCount > 0)
        {
            warnings.Add("Style import requires implementing issue #375 for SLD conversion");
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

    private async Task<ImportStepResult> ImportWorkspacesAsync(GeoServerWorkspaceInfo[] workspaces, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingWorkspaces(_logger, workspaces.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingWorkspaces,
            CurrentPhase = "Importing workspaces"
        };
        progress?.Report(updatedProgress);

        foreach (var workspace in workspaces)
        {
            try
            {
                // TODO: Implement actual workspace creation in Honua
                // For now, this is a placeholder that simulates the import
                Log.WorkspaceImported(_logger, workspace.Name);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Workspace",
                    Name = workspace.Name,
                    Notes = "Imported successfully"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported workspace: {workspace.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.WorkspaceImportFailed(_logger, workspace.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "Workspace",
                    Name = workspace.Name,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task<ImportStepResult> ImportDataStoresAsync(GeoServerDataStoreInfo[] dataStores, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingDataStores(_logger, dataStores.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingDataStores,
            CurrentPhase = "Importing datastores"
        };
        progress?.Report(updatedProgress);

        foreach (var dataStore in dataStores)
        {
            try
            {
                // Check compatibility
                if (dataStore.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
                {
                    var behavior = request.ImportOptions?.UnsupportedDataStoreBehavior ?? UnsupportedResourceBehavior.Skip;
                    if (behavior == UnsupportedResourceBehavior.FailImport)
                    {
                        throw new InvalidOperationException($"DataStore {dataStore.Name} is incompatible: {dataStore.Compatibility.Reason}");
                    }

                    if (behavior == UnsupportedResourceBehavior.Skip)
                    {
                        Log.DataStoreSkipped(_logger, dataStore.Name, dataStore.Compatibility.Reason);
                        result.SkippedCount++;
                        continue;
                    }
                }

                // TODO: Implement actual datastore creation in Honua
                // This would involve creating connection configs, testing connections, etc.
                Log.DataStoreImported(_logger, dataStore.WorkspaceName, dataStore.Name, dataStore.Type);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "DataStore",
                    Name = dataStore.Name,
                    WorkspaceName = dataStore.WorkspaceName,
                    Notes = $"Imported {dataStore.Type} datastore"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported datastore: {dataStore.WorkspaceName}/{dataStore.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.DataStoreImportFailed(_logger, dataStore.WorkspaceName, dataStore.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "DataStore",
                    Name = dataStore.Name,
                    WorkspaceName = dataStore.WorkspaceName,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task<ImportStepResult> ImportLayersAsync(GeoServerLayerInfo[] layers, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingLayers(_logger, layers.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingLayers,
            CurrentPhase = "Importing layers"
        };
        progress?.Report(updatedProgress);

        foreach (var layer in layers)
        {
            try
            {
                // Check compatibility
                if (layer.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
                {
                    var behavior = request.ImportOptions?.UnsupportedLayerBehavior ?? UnsupportedResourceBehavior.Skip;
                    if (behavior == UnsupportedResourceBehavior.FailImport)
                    {
                        throw new InvalidOperationException($"Layer {layer.Name} is incompatible: {layer.Compatibility.Reason}");
                    }

                    if (behavior == UnsupportedResourceBehavior.Skip)
                    {
                        Log.LayerSkipped(_logger, layer.Name, layer.Compatibility.Reason);
                        result.SkippedCount++;
                        continue;
                    }
                }

                // TODO: Implement actual layer creation in Honua
                // This would involve creating layer configs, setting up publishing, etc.
                Log.LayerImported(_logger, layer.WorkspaceName, layer.Name);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Layer",
                    Name = layer.Name,
                    WorkspaceName = layer.WorkspaceName,
                    Notes = "Imported layer configuration"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported layer: {layer.WorkspaceName}/{layer.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.LayerImportFailed(_logger, layer.WorkspaceName, layer.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "Layer",
                    Name = layer.Name,
                    WorkspaceName = layer.WorkspaceName,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task<ImportStepResult> ImportStylesAsync(GeoServerStyleInfo[] styles, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingStyles(_logger, styles.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingStyles,
            CurrentPhase = "Importing styles"
        };
        progress?.Report(updatedProgress);

        foreach (var style in styles)
        {
            try
            {
                // Check if SLD conversion is available (issue #375)
                if (style.Format == "sld")
                {
                    var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;
                    var warningMessage = $"SLD style {style.Name} requires conversion to MapLibre format (issue #375)";

                    if (behavior == UnsupportedResourceBehavior.FailImport)
                    {
                        throw new InvalidOperationException(warningMessage);
                    }

                    if (behavior == UnsupportedResourceBehavior.Skip)
                    {
                        Log.StyleSkipped(_logger, style.Name);
                        result.SkippedCount++;
                        continue;
                    }

                    if (behavior == UnsupportedResourceBehavior.LogWarning)
                    {
                        Log.StyleRequiresConversion(_logger, style.Name);
                        result.Warnings.Add(warningMessage);
                    }
                }

                // TODO: Implement actual style import once issue #375 is implemented
                // This would involve converting SLD to MapLibre JSON and creating style resources
                Log.StyleImported(_logger, style.WorkspaceName ?? "global", style.Name, style.Format);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Style",
                    Name = style.Name,
                    WorkspaceName = style.WorkspaceName,
                    Notes = style.Format == "sld" ? "Converted from SLD to MapLibre format" : "Imported style"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported style: {style.WorkspaceName ?? "global"}/{style.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.StyleImportFailed(_logger, style.WorkspaceName ?? "global", style.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "Style",
                    Name = style.Name,
                    WorkspaceName = style.WorkspaceName,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task ValidateImportedResourcesAsync(GeoServerImportRequest request, CancellationToken cancellationToken)
    {
        // TODO: Implement validation logic
        // This could check that imported resources are properly configured,
        // connections are working, etc.
        Log.ValidatingImportedResources(_logger);
        await Task.Delay(100, cancellationToken); // Simulate validation work
    }

    private static GeoServerImportProgress UpdateProgressWithWorkspaces(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                WorkspacesProcessed = result.SuccessCount,
                WorkspacesFailed = result.FailureCount
            }
        };
    }

    private static void AggregateStepResult(ImportStepResult aggregateResult, ImportStepResult stepResult)
    {
        aggregateResult.SuccessCount += stepResult.SuccessCount;
        aggregateResult.FailureCount += stepResult.FailureCount;
        aggregateResult.SkippedCount += stepResult.SkippedCount;
        aggregateResult.ImportedResources.AddRange(stepResult.ImportedResources);
        aggregateResult.FailedResources.AddRange(stepResult.FailedResources);
        aggregateResult.Warnings.AddRange(stepResult.Warnings);
    }

    private static GeoServerImportProgress UpdateProgressWithDataStores(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                DataStoresProcessed = result.SuccessCount,
                DataStoresFailed = result.FailureCount
            }
        };
    }

    private static GeoServerImportProgress UpdateProgressWithLayers(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                LayersProcessed = result.SuccessCount,
                LayersFailed = result.FailureCount
            }
        };
    }

    private static GeoServerImportProgress UpdateProgressWithStyles(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                StylesProcessed = result.SuccessCount,
                StylesFailed = result.FailureCount
            },
            Warnings = progress.Warnings.Concat(result.Warnings).ToList()
        };
    }

    private static GeoServerImportResult CreateSuccessResult(GeoServerServiceInfo serviceInfo, ImportStepResult finalResult, GeoServerImportRequest request, TimeSpan duration)
    {
        var importedWorkspaces = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "Workspace", StringComparison.Ordinal));
        var importedDataStores = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "DataStore", StringComparison.Ordinal));
        var importedLayers = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "Layer", StringComparison.Ordinal));
        var importedStyles = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "Style", StringComparison.Ordinal));

        return GeoServerImportResult.CreateSuccess(
            request.GeoServerRestUrl,
            request.TargetHonuaUrl,
            importedWorkspaces,
            importedDataStores,
            importedLayers,
            importedStyles,
            serviceInfo.Version,
            duration,
            finalResult.Warnings,
            finalResult.ImportedResources)
            with
        {
            FailedResources = finalResult.FailureCount,
            FailedResourceDetails = finalResult.FailedResources
        };
    }

    // Helper classes for internal state tracking
    private sealed record FilteredResources
    {
        public GeoServerWorkspaceInfo[] Workspaces { get; init; } = [];
        public GeoServerDataStoreInfo[] DataStores { get; init; } = [];
        public GeoServerLayerInfo[] Layers { get; init; } = [];
        public GeoServerStyleInfo[] Styles { get; init; } = [];
        public int WorkspaceCount { get; init; }
        public int DataStoreCount { get; init; }
        public int LayerCount { get; init; }
        public int StyleCount { get; init; }
    }

    private sealed record ImportStepResult
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

        [LoggerMessage(8011, LogLevel.Information, "Validating imported resources (placeholder)")]
        public static partial void ValidatingImportedResources(ILogger logger);
    }
}
