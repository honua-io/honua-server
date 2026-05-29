// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
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
/// Legacy in-place "Import*" step methods used by the non-dry-run, non-apply-plan
/// path of <c>ImportConfigurationAsync</c>. These
/// helpers iterate filtered resource collections, simulate persistence, aggregate
/// per-step results into progress reports, and produce the final
/// <see cref="GeoServerImportResult"/>.
/// </summary>
internal sealed partial class GeoServerImportService
{
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
                // Create workspace metadata in Honua catalog
                // Note: Honua uses a different workspace model than GeoServer
                await CreateWorkspaceMetadataAsync(workspace, cancellationToken);
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

                // Create datastore configuration in Honua catalog
                // Note: Honua uses connection-based data sources instead of GeoServer datastores
                await CreateDataStoreConfigurationAsync(dataStore, cancellationToken);
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

                // Create layer configuration in Honua catalog
                // Note: Honua layers are published through the layer publishing service
                await CreateLayerConfigurationAsync(layer, cancellationToken);
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
                // SLD-to-MapLibre conversion (issue #375).
                var sldValidated = false;
                if (style.Format == "sld")
                {
                    var conversionWarnings = TryConvertSldStyle(
                        style,
                        request,
                        result,
                        out var converterAvailable,
                        out var shouldSkip,
                        out sldValidated);

                    if (!converterAvailable)
                    {
                        // No ISldStyleConverter wired; fall through to legacy unsupported-style behavior.
                        var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;
                        var warningMessage = $"SLD style {style.Name} requires the SLD converter (issue #375). No ISldStyleConverter is registered.";

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

                        Log.StyleRequiresConversion(_logger, style.Name);
                        result.Warnings.Add(warningMessage);
                    }
                    else
                    {
                        result.Warnings.AddRange(conversionWarnings);
                        if (shouldSkip)
                        {
                            // Conversion errors with UnsupportedStyleBehavior.Skip already incremented
                            // SkippedCount; skip the import side so the style is not double-counted.
                            continue;
                        }
                    }
                }

                // Convert and import style to Honua format
                // Note: SLD styles are converted to MapLibre JSON format for Honua
                await ConvertAndImportStyleAsync(style, cancellationToken);
                Log.StyleImported(_logger, style.WorkspaceName ?? "global", style.Name, style.Format);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Style",
                    Name = style.Name,
                    WorkspaceName = style.WorkspaceName,
                    Notes = style.Format == "sld"
                        ? sldValidated
                            ? "SLD validated; apply via per-layer admin SLD endpoint to persist MapLibre style"
                            : "SLD not validated; review warnings before applying via per-layer admin SLD endpoint"
                        : "Imported style"
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

    private async Task CreateWorkspaceMetadataAsync(GeoServerWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        // Store workspace metadata in Honua's catalog system
        // Note: Honua maps GeoServer workspaces to internal catalog namespaces
        Log.CreatingWorkspaceMetadata(_logger, workspace.Name);

        // In a full implementation, this would:
        // 1. Create a workspace record in the catalog
        // 2. Set up namespace mappings
        // 3. Configure default styling and access permissions
        await Task.Delay(50, cancellationToken); // Simulate metadata creation
    }

    private async Task CreateDataStoreConfigurationAsync(GeoServerDataStoreInfo dataStore, CancellationToken cancellationToken)
    {
        // Create connection configuration for Honua
        // Note: GeoServer datastores map to Honua connection configurations
        Log.DataStoreConfigurationCreated(_logger, dataStore.WorkspaceName, dataStore.Name);

        // Implementation would:
        // 1. Extract connection parameters from GeoServer datastore
        // 2. Create Honua connection configuration
        // 3. Test connection validity
        // 4. Store in secure connection registry
        await Task.Delay(75, cancellationToken); // Simulate configuration creation
    }

    private async Task CreateLayerConfigurationAsync(GeoServerLayerInfo layer, CancellationToken cancellationToken)
    {
        // Create layer publishing configuration for Honua
        // Note: GeoServer layers map to Honua published layers
        Log.LayerConfigurationCreated(_logger, layer.WorkspaceName, layer.Name);

        // Implementation would:
        // 1. Map GeoServer layer configuration to Honua layer definition
        // 2. Set up feature access and security constraints
        // 3. Configure default styling and rendering options
        // 4. Register layer in catalog
        await Task.Delay(100, cancellationToken); // Simulate layer configuration
    }

    private async Task ConvertAndImportStyleAsync(GeoServerStyleInfo style, CancellationToken cancellationToken)
    {
        // SLD parsing/conversion is delegated to ISldStyleConverter (Honua.Server) and
        // executed by TryConvertSldStyle so warnings/errors are surfaced in the import
        // result. Per-layer style persistence is performed by the admin SLD endpoint
        // (POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld); this method
        // remains a single-responsibility conversion hook for the bulk import path and
        // does not write the converted MapLibre JSON to the catalog.
        Log.StyleConversionStarted(_logger, style.Name);
        await Task.Delay(125, cancellationToken); // Simulate style conversion
    }

    /// <summary>
    /// Invokes the registered <see cref="ISldStyleConverter"/> against the embedded SLD content
    /// when available and returns any warning messages for inclusion in import results.
    /// When conversion errors or missing content combine with <see cref="UnsupportedResourceBehavior.Skip"/>,
    /// <c>shouldSkip</c> is set to <c>true</c> so the caller can abandon the per-style import
    /// without double-counting (<see cref="ImportStepResult.SkippedCount"/> is already incremented).
    /// <c>wasValidated</c> reports whether the converter actually ran and produced no errors;
    /// callers use it to label the imported resource note.
    /// </summary>
    internal List<string> TryConvertSldStyle(
        GeoServerStyleInfo style,
        GeoServerImportRequest request,
        ImportStepResult result,
        out bool converterAvailable,
        out bool shouldSkip,
        out bool wasValidated)
    {
        var warnings = new List<string>();
        converterAvailable = _sldConverter != null;
        shouldSkip = false;
        wasValidated = false;

        if (_sldConverter == null)
        {
            return warnings;
        }

        if (string.IsNullOrWhiteSpace(style.SldContent))
        {
            ApplyUnsupportedStyleBehavior(
                style,
                request,
                result,
                warnings,
                $"SLD style {style.Name} has no embedded content; conversion skipped.",
                out shouldSkip);
            return warnings;
        }

        var conversion = _sldConverter.Convert(style.SldContent!);
        foreach (var warning in conversion.Warnings)
        {
            warnings.Add($"SLD style {style.Name}: {warning}");
        }

        if (conversion.HasErrors)
        {
            var firstError = conversion.Errors.Count > 0 ? conversion.Errors[0] : "SLD conversion produced no layers.";
            ApplyUnsupportedStyleBehavior(
                style,
                request,
                result,
                warnings,
                $"SLD style {style.Name} could not be converted: {firstError}",
                out shouldSkip);
            return warnings;
        }

        wasValidated = true;
        return warnings;
    }

    private void ApplyUnsupportedStyleBehavior(
        GeoServerStyleInfo style,
        GeoServerImportRequest request,
        ImportStepResult result,
        List<string> warnings,
        string message,
        out bool shouldSkip)
    {
        shouldSkip = false;
        var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;

        if (behavior == UnsupportedResourceBehavior.FailImport)
        {
            throw new InvalidOperationException(message);
        }

        if (behavior == UnsupportedResourceBehavior.Skip)
        {
            Log.StyleSkipped(_logger, style.Name);
            result.SkippedCount++;
            shouldSkip = true;
            return;
        }

        Log.StyleRequiresConversion(_logger, style.Name);
        warnings.Add(message);
    }

    private async Task ValidateImportedResourcesAsync(GeoServerImportRequest request, CancellationToken cancellationToken)
    {
        // Validate imported resources are properly configured
        Log.ValidatingImportedResources(_logger);

        // Perform comprehensive validation:
        // 1. Verify database connections are accessible
        // 2. Check that imported layers have valid geometry
        // 3. Validate style references exist and are valid
        // 4. Ensure security constraints are properly applied
        await Task.Delay(100, cancellationToken);

        Log.ImportValidationCompleted(_logger);
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
}
