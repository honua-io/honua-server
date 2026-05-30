// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    /// <inheritdoc />
    public async Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUrl = ArcGisRestClient.NormalizeServiceUrl(request.ServiceUrl);

        try
        {
            using var serviceDocument = await _restClient.GetJsonDocumentAsync(
                $"{normalizedUrl}?f=json",
                ResiliencePolicyOptions.Default.MaxRetryAttempts,
                request.TimeoutSeconds,
                request.Credentials,
                cancellationToken).ConfigureAwait(false);

            if (TryReadArcGisError(serviceDocument.RootElement, out var errorCode, out var errorMessage))
            {
                var (authMode, code) = ClassifyArcGisError(errorCode, request.Credentials);
                return CreateFailedScanArtifact(
                    normalizedUrl,
                    authMode,
                    code,
                    errorMessage,
                    serviceType: ExtractServiceType(normalizedUrl),
                    credentialsSupplied: HasCredentialMaterial(request.Credentials));
            }

            var serviceKey = GetServiceKey(normalizedUrl);
            var serviceDisplayName = GetServiceDisplayName(serviceDocument.RootElement, serviceKey);
            var containerId = $"service:{serviceKey}";
            var serviceCapabilities = SplitCsv(GetOptionalStringProperty(serviceDocument.RootElement, "capabilities"));
            var completenessWarnings = new List<string>();
            var missingArtifacts = new List<string>();
            var resources = new List<MigrationInventoryResource>();
            var styles = new List<MigrationInventoryStyle>();
            var dependencies = new List<MigrationExternalDependency>();
            var fidelityClassifications = new List<MigrationFidelityClassificationRecord>();
            fidelityClassifications.AddRange(BuildServiceFidelityClassifications(
                containerId,
                serviceKey,
                serviceDisplayName,
                ExtractServiceType(normalizedUrl),
                serviceCapabilities));

            foreach (var resourceReference in EnumerateResourceReferences(serviceDocument.RootElement))
            {
                try
                {
                    var resourceResult = await BuildScanResourceAsync(
                        normalizedUrl,
                        containerId,
                        serviceKey,
                        resourceReference,
                        serviceCapabilities,
                        request.TimeoutSeconds,
                        request.Credentials,
                        cancellationToken).ConfigureAwait(false);

                    resources.Add(resourceResult.Resource);
                    if (resourceResult.Style != null)
                    {
                        styles.Add(resourceResult.Style);
                    }

                    dependencies.AddRange(resourceResult.Dependencies);
                    fidelityClassifications.AddRange(resourceResult.FidelityClassifications);
                    completenessWarnings.AddRange(resourceResult.Warnings);
                    missingArtifacts.AddRange(resourceResult.MissingArtifacts);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.InventoryResourceScanFailed(_logger, normalizedUrl, resourceReference.Id, resourceReference.Kind, ex);
                    completenessWarnings.Add($"Failed to scan {resourceReference.Kind} {resourceReference.Id}: {ex.Message}");
                    missingArtifacts.Add($"{resourceReference.Kind}:{resourceReference.Id}");
                }
            }

            var orderedResources = resources.OrderBy(static resource => resource.Id, StringComparer.Ordinal).ToArray();
            var orderedStyles = styles.OrderBy(static style => style.Id, StringComparer.Ordinal).ToArray();
            var orderedDependencies = dependencies.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).ToArray();
            var orderedFidelityClassifications = fidelityClassifications
                .OrderBy(static record => record.Id, StringComparer.Ordinal)
                .ToArray();

            var containerAssessment = MigrationInventoryHelpers.Aggregate(
                orderedResources.Select(resource => resource.Compatibility)
                    .Concat(orderedStyles.Select(style => style.Compatibility))
                    .Concat(orderedDependencies.Select(dependency => dependency.Compatibility)),
                "No GeoServices resources were discovered.");

            if (HasMixedRendererCodes(orderedStyles))
            {
                containerAssessment = containerAssessment with
                {
                    Code = ImportCompatibilityCodes.ArcGisMixedRenderers
                };
            }

            var containers = new[]
            {
                new MigrationInventoryContainer
                {
                    Id = containerId,
                    Kind = "service",
                    Name = serviceKey,
                    Title = serviceDisplayName,
                    Description = GetOptionalStringProperty(serviceDocument.RootElement, "description"),
                    Compatibility = containerAssessment
                }
            };

            if (orderedResources.Length == 0 && !missingArtifacts.Contains("resources", StringComparer.Ordinal))
            {
                missingArtifacts.Add("resources");
            }

            var summary = MigrationInventoryHelpers.BuildSummary(containers, orderedResources, orderedStyles, orderedDependencies);
            var overallCompatibility = MigrationInventoryHelpers.Aggregate(
                containers.Select(container => container.Compatibility)
                    .Concat(orderedResources.Select(resource => resource.Compatibility))
                    .Concat(orderedStyles.Select(style => style.Compatibility))
                    .Concat(orderedDependencies.Select(dependency => dependency.Compatibility)),
                "No inventory items were discovered.");

            var completeness = MigrationInventoryHelpers.BuildCompleteness(
                completenessWarnings.Count == 0
                    ? "complete"
                    : orderedResources.Length > 0 || orderedStyles.Length > 0 || orderedDependencies.Length > 0
                        ? "partial"
                        : "failed",
                completenessWarnings,
                missingArtifacts);

            return new MigrationSourceInventoryArtifact
            {
                SourceKind = "arcgis-geoservices-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = serviceDisplayName,
                    BaseUrl = normalizedUrl,
                    Product = "ArcGIS GeoServices REST",
                    Version = FormatVersion(serviceDocument.RootElement.TryGetProperty("currentVersion", out var versionElement) ? versionElement : default),
                    ServiceType = ExtractServiceType(normalizedUrl)
                },
                AuthPosture = BuildAuthPosture(request.Credentials, accessConfirmed: true),
                ScanCompleteness = completeness,
                Summary = summary,
                OverallCompatibility = overallCompatibility,
                Containers = containers,
                Resources = orderedResources,
                Styles = orderedStyles,
                ExternalDependencies = orderedDependencies,
                FidelityClassifications = orderedFidelityClassifications,
                FidelityMatrix = MigrationFidelityMatrixBuilder.Build(orderedFidelityClassifications)
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Log.InventoryScanFailed(_logger, normalizedUrl, ex);
            return CreateFailedScanArtifact(
                normalizedUrl,
                ex.StatusCode == HttpStatusCode.Forbidden || HasCredentialMaterial(request.Credentials)
                    ? GeoservicesAuthenticationModes.Denied
                    : GeoservicesAuthenticationModes.AuthRequired,
                ex.StatusCode == HttpStatusCode.Forbidden
                    ? ImportCompatibilityCodes.ArcGisAccessDenied
                    : ImportCompatibilityCodes.ArcGisTokenRequired,
                "The ArcGIS service requires authentication for discovery.",
                ExtractServiceType(normalizedUrl),
                HasCredentialMaterial(request.Credentials));
        }
        catch (InvalidOperationException ex)
        {
            Log.InventoryScanFailed(_logger, normalizedUrl, ex);
            return CreateFailedScanArtifact(
                normalizedUrl,
                "unknown",
                ImportCompatibilityCodes.ArcGisServiceError,
                ex.Message,
                ExtractServiceType(normalizedUrl),
                HasCredentialMaterial(request.Credentials));
        }
    }

    private async Task<GeoservicesScanResourceResult> BuildScanResourceAsync(
        string normalizedUrl,
        string containerId,
        string serviceName,
        GeoservicesResourceReference resourceReference,
        string[] serviceCapabilities,
        int timeoutSeconds,
        GeoservicesCredentialDescriptor? credentials,
        CancellationToken cancellationToken)
    {
        using var resourceDocument = await _restClient.GetJsonDocumentAsync(
            $"{normalizedUrl}/{resourceReference.Id}?f=json",
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            timeoutSeconds,
            credentials,
            cancellationToken).ConfigureAwait(false);

        if (TryReadArcGisError(resourceDocument.RootElement, out _, out var resourceError))
        {
            throw new InvalidOperationException(resourceError);
        }

        var resourceCapabilities = SplitCsv(GetOptionalStringProperty(resourceDocument.RootElement, "capabilities"));
        var advertisedCapabilities = resourceCapabilities.Length == 0 ? serviceCapabilities : resourceCapabilities;
        var geometryType = GetOptionalStringProperty(resourceDocument.RootElement, "geometryType");
        var hasAttachments = GetOptionalBoolProperty(resourceDocument.RootElement, "hasAttachments");

        int? featureCount = null;
        try
        {
            featureCount = await TryGetFeatureCountAsync(
                normalizedUrl,
                resourceReference.Id,
                timeoutSeconds,
                credentials,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.InventoryFeatureCountFailed(_logger, normalizedUrl, resourceReference.Id, ex);
        }

        var spatialReferences = await BuildArcGisSpatialReferencesAsync(resourceDocument.RootElement, cancellationToken).ConfigureAwait(false);

        var fields = ExtractFieldMetadata(
            resourceDocument.RootElement,
            out var fieldWarnings);

        Log.InventoryFieldsExtracted(
            _logger,
            normalizedUrl,
            resourceReference.Id,
            fields.Length);

        var style = BuildRendererStyle(
            containerId,
            serviceName,
            resourceReference,
            resourceDocument.RootElement,
            out var rendererWarnings,
            out var rendererDependencies);

        var dependencies = new List<MigrationExternalDependency>(rendererDependencies);
        if (hasAttachments == true)
        {
            dependencies.Add(new MigrationExternalDependency
            {
                Id = $"dependency:{serviceName}:{resourceReference.Kind}:{resourceReference.Id}:attachments",
                ContainerId = containerId,
                ResourceId = GetResourceId(serviceName, resourceReference),
                Kind = "attachments",
                Name = resourceReference.Name,
                DependencyType = "attachments",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["advertised"] = "true"
                },
                SpatialReferences = [],
                Compatibility = MigrationInventoryHelpers.Partial(
                    "Attachments require a separate migration path.",
                    ["The source resource advertises attachments."],
                    ["Plan a separate attachment migration alongside the core data import."],
                    code: ImportCompatibilityCodes.ArcGisAttachments)
            });
        }

        var resourceWarnings = new List<string>(rendererWarnings);
        resourceWarnings.AddRange(fieldWarnings);
        var missingArtifacts = new List<string>();

        if (featureCount == null)
        {
            resourceWarnings.Add($"Feature count was unavailable for {resourceReference.Kind} {resourceReference.Id}.");
            missingArtifacts.Add($"{resourceReference.Kind}:{resourceReference.Id}:feature-count");
        }

        var compatibility = BuildResourceCompatibility(
            resourceReference.Kind,
            geometryType,
            advertisedCapabilities,
            hasAttachments,
            spatialReferences.Length > 0);

        var resource = new MigrationInventoryResource
        {
            Id = GetResourceId(serviceName, resourceReference),
            ContainerId = containerId,
            Kind = resourceReference.Kind,
            Name = resourceReference.Name,
            Title = GetOptionalStringProperty(resourceDocument.RootElement, "name") ?? resourceReference.Name,
            Description = GetOptionalStringProperty(resourceDocument.RootElement, "description"),
            GeometryType = geometryType,
            FeatureCount = featureCount,
            HasAttachments = hasAttachments,
            Capabilities = advertisedCapabilities.OrderBy(static capability => capability, StringComparer.Ordinal).ToArray(),
            SpatialReferences = spatialReferences,
            Fields = fields,
            StyleIds = style == null ? [] : [style.Id],
            ExternalDependencyIds = dependencies.Select(dependency => dependency.Id)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            Compatibility = compatibility
        };
        var fidelityClassifications = BuildResourceFidelityClassifications(
            resource,
            resourceDocument.RootElement,
            style,
            dependencies);

        return new GeoservicesScanResourceResult(
            resource,
            style,
            dependencies.ToArray(),
            fidelityClassifications,
            MigrationInventoryHelpers.NormalizeStrings(resourceWarnings),
            MigrationInventoryHelpers.NormalizeStrings(missingArtifacts));
    }

    private static MigrationSourceInventoryArtifact CreateFailedScanArtifact(
        string normalizedUrl,
        string authMode,
        string compatibilityCode,
        string message,
        string serviceType,
        bool credentialsSupplied)
    {
        var manualSteps = compatibilityCode is ImportCompatibilityCodes.ArcGisTokenRequired or ImportCompatibilityCodes.ArcGisTokenExpired
            ? new[] { "Provide a valid ArcGIS token or credentials and rerun the scan." }
            : compatibilityCode == ImportCompatibilityCodes.ArcGisAccessDenied
                ? ["Confirm the supplied identity has read access to the service and rerun the scan."]
                : new[] { "Verify service reachability, access, and metadata exposure before rerunning the scan." };

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = ExtractServiceName(normalizedUrl),
                BaseUrl = normalizedUrl,
                Product = "ArcGIS GeoServices REST",
                ServiceType = serviceType
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = authMode,
                CredentialsSupplied = credentialsSupplied,
                AccessConfirmed = false,
                Notes = MigrationInventoryHelpers.NormalizeStrings([message])
            },
            ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness(
                "failed",
                [message],
                ["source-inventory"]),
            Summary = new MigrationInventorySummary(),
            OverallCompatibility = MigrationInventoryHelpers.Partial(
                "The scan did not complete successfully.",
                [message],
                manualSteps,
                code: compatibilityCode),
            Containers = [],
            Resources = [],
            Styles = [],
            ExternalDependencies = []
        };
    }

    private static MigrationInventoryAuthPosture BuildAuthPosture(
        GeoservicesCredentialDescriptor? credentials,
        bool accessConfirmed,
        string[]? notes = null)
    {
        var mode = credentials?.GetNormalizedMode() ?? GeoservicesAuthenticationModes.Anonymous;
        return new MigrationInventoryAuthPosture
        {
            Mode = mode,
            CredentialsSupplied = HasCredentialMaterial(credentials),
            AccessConfirmed = accessConfirmed,
            Notes = MigrationInventoryHelpers.NormalizeStrings(notes ?? [])
        };
    }
}
