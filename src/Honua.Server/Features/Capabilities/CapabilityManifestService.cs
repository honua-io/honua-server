// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Honua.Core.Configuration;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Capabilities.Models;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;
using Honua.ControlPlane;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Server.Features.Streaming;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Capabilities;

internal interface ICapabilityManifestService
{
    Task<CapabilityManifestDocument> GetManifestAsync(
        CapabilityManifestRequest request,
        CancellationToken cancellationToken = default);
}

internal readonly record struct CapabilityManifestRequest(
    ClaimsPrincipal Principal,
    string? TenantId,
    TenantContextSource TenantSource,
    string? Environment,
    string? WorkspaceId,
    bool Authenticated);

internal sealed class CapabilityManifestService(
    IOptions<LimitsOptions> limitsOptions,
    IOptions<FeatureStreamOptions> streamOptions,
    IOptions<FeatureChangeEventOptions> eventOptions,
    IOptions<ClientCertificateAuthenticationOptions> clientCertificateOptions,
    IOptions<ControlPlaneOptions> controlPlaneOptions,
    IOptions<FileUploadOptions> fileUploadOptions,
    IOptions<FileUploadSecurityOptions> fileUploadSecurityOptions,
    IOptions<GrpcOptions> grpcOptions,
    IOptions<AlertOptions> alertOptions,
    IOptions<RbacOptions> rbacOptions,
    ILicenseEntitlementService entitlementService,
    IConsoleActionEvaluator consoleActionEvaluator,
    IMetadataV2EnvironmentSnapshotReader environmentSnapshotReader,
    IEnumerable<IBatchComputeBackend> batchBackends,
    IServiceProvider serviceProvider,
    IWebHostEnvironment hostEnvironment,
    ILogger<CapabilityManifestService> logger) : ICapabilityManifestService
{
    private const string AuthorizationNotice =
        "Manifest availability is informational only; operation endpoints remain the source of truth for authorization, tenant, environment, license, and resource checks.";

    private static readonly string[] SpatialAnalyticsEntitlementKeys =
    [
        "analytics.clustering",
        "analytics.spatial-join",
        "analytics.buffer-aggregate",
        "analytics.density"
    ];

    public async Task<CapabilityManifestDocument> GetManifestAsync(
        CapabilityManifestRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "honua.capabilities.manifest",
            ActivityKind.Server);
        activity?.SetTag("honua.tenant.source", request.TenantSource.ToString());
        activity?.SetTag("honua.capabilities.environment_requested", !string.IsNullOrWhiteSpace(request.Environment));
        activity?.SetTag("honua.capabilities.workspace_requested", !string.IsNullOrWhiteSpace(request.WorkspaceId));
        activity?.SetTag("honua.capabilities.authenticated", request.Authenticated);

        var snapshot = entitlementService.GetSnapshot();
        var callerCapabilities = await consoleActionEvaluator
            .ResolveCapabilitiesAsync(request.Principal, cancellationToken)
            .ConfigureAwait(false);
        var callerCapabilitySet = new HashSet<string>(callerCapabilities, StringComparer.Ordinal);
        var environment = await ResolveEnvironmentAsync(request.Environment, cancellationToken).ConfigureAwait(false);
        var workspaceAvailable = ResolveWorkspaceAvailability(request.Principal, request.WorkspaceId, request.Authenticated);
        var policyContext = new CapabilityPolicyContext(
            snapshot,
            callerCapabilitySet,
            request.Authenticated,
            environment.Available,
            workspaceAvailable,
            request.WorkspaceId);
        var batchCapabilities = await ResolveBatchCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        var capabilities = BuildCapabilities(policyContext);
        var unavailableCount = capabilities.Count(static c => !c.Available);
        var manifest = new CapabilityManifestDocument
        {
            IssuedAt = DateTimeOffset.UtcNow,
            Scope = new CapabilityManifestScope
            {
                TenantId = request.TenantId,
                TenantSource = request.TenantSource.ToString(),
                Environment = request.Environment,
                WorkspaceId = request.WorkspaceId,
                WorkspaceAvailable = workspaceAvailable,
                WorkspaceReasonCode = request.WorkspaceId is null || workspaceAvailable
                    ? null
                    : CapabilityReasonCodes.InsufficientPolicy,
                Authenticated = request.Authenticated
            },
            Server = BuildServerInfo(),
            Environment = environment,
            Packages = BuildPackages(),
            Capabilities = capabilities,
            Transports = BuildTransports(),
            Limits = BuildLimits(batchCapabilities),
            Policies = BuildPolicies(snapshot, callerCapabilities),
            Links = BuildLinks()
        };

        activity?.SetTag("honua.license.edition", snapshot.Edition.ToString());
        activity?.SetTag("honua.capabilities.count", capabilities.Length);
        activity?.SetTag("honua.capabilities.unavailable_count", unavailableCount);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var tenantSource = request.TenantSource.ToString();
            var environmentRequested = !string.IsNullOrWhiteSpace(request.Environment);
            var workspaceRequested = !string.IsNullOrWhiteSpace(request.WorkspaceId);
            CapabilityManifestLog.ManifestGenerated(
                logger,
                tenantSource,
                environmentRequested,
                workspaceRequested,
                request.Authenticated,
                capabilities.Length,
                unavailableCount);
        }

        return manifest;
    }

    private async ValueTask<CapabilityManifestEnvironment> ResolveEnvironmentAsync(
        string? environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return new CapabilityManifestEnvironment
            {
                Requested = false,
                Available = true
            };
        }

        try
        {
            var snapshot = await environmentSnapshotReader
                .GetCurrentAsync(environment, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return new CapabilityManifestEnvironment
                {
                    EnvironmentId = environment,
                    Requested = true,
                    Available = false,
                    ReasonCode = CapabilityReasonCodes.EnvironmentUnavailable
                };
            }

            return new CapabilityManifestEnvironment
            {
                EnvironmentId = snapshot.Graph.Environment,
                Requested = true,
                Available = true,
                Revision = snapshot.Revision,
                LoadedAt = snapshot.LoadedAt
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CapabilityManifestLog.EnvironmentSnapshotFailed(logger, environment, ex);
            return new CapabilityManifestEnvironment
            {
                EnvironmentId = environment,
                Requested = true,
                Available = false,
                ReasonCode = CapabilityReasonCodes.EnvironmentUnavailable
            };
        }
    }

    private CapabilityManifestServerInfo BuildServerInfo()
    {
        var assembly = typeof(CapabilityManifestService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new CapabilityManifestServerInfo
        {
            ServerVersion = string.IsNullOrWhiteSpace(version)
                ? assembly.GetName().Version?.ToString() ?? "0.0.0"
                : version,
            ApiVersion = "v1",
            MetadataApiVersion = MetadataV2Constants.ApiVersion,
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            DeploymentEnvironment = hostEnvironment.EnvironmentName
        };
    }

    private static CapabilityManifestPackages BuildPackages()
    {
        return new CapabilityManifestPackages
        {
            SchemaVersions =
            [
                CapabilityManifestConstants.SchemaVersion,
                MetadataV2Constants.ApiVersion,
                MetadataV2Constants.SchemaVersion,
                "honua_map_package.v1",
                "honua_app_package.v1"
            ],
            Families =
            [
                new CapabilityManifestPackageFamily
                {
                    Id = "metadata-v2-graph",
                    Kind = "metadata",
                    SchemaVersion = MetadataV2Constants.SchemaVersion,
                    Supported = true
                },
                new CapabilityManifestPackageFamily
                {
                    Id = "metadata-release-package",
                    Kind = "publication",
                    SchemaVersion = MetadataV2Constants.ApiVersion,
                    Supported = true
                },
                new CapabilityManifestPackageFamily
                {
                    Id = "gitops-metadata-release-manifest",
                    Kind = "gitops",
                    SchemaVersion = MetadataV2Constants.ApiVersion,
                    Supported = true
                },
                new CapabilityManifestPackageFamily
                {
                    Id = "map-package",
                    Kind = "map",
                    SchemaVersion = "honua_map_package.v1",
                    Supported = true
                },
                new CapabilityManifestPackageFamily
                {
                    Id = "app-package",
                    Kind = "app",
                    SchemaVersion = "honua_app_package.v1",
                    Supported = true
                }
            ],
            StorageFamilies = Enum.GetValues<MetadataV2StorageType>()
                .Select(ToWireValue)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PublicationFamilies = Enum.GetValues<MetadataV2PublicationType>()
                .Select(ToWireValue)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private CapabilityManifestCapability[] BuildCapabilities(CapabilityPolicyContext context)
    {
        var alertOptionsValue = alertOptions.Value;
        var mtlsOptions = clientCertificateOptions.Value;
        var syncSupported = IsFieldCollectionSyncSupported();
        var deployTargetCount = controlPlaneOptions.Value.DeployTargets.Count;
        var workloadCount = controlPlaneOptions.Value.ExecutionWorkloads.Count;

        return
        [
            Capability("package.metadata-v2", "packages", context),
            Capability("package.release-package", "packages", context, policyCapability: "catalog.publish", requiresEnvironment: true),
            Capability("package.gitops-manifest", "packages", context, policyCapability: "catalog.publish", requiresEnvironment: true),
            Capability("package.map", "packages", context, policyCapability: "studio.edit"),
            Capability("package.app", "packages", context, policyCapability: "studio.edit"),

            Capability("temporal.filtering", "temporal", context, entitlementKey: "temporal.filtering"),
            Capability("temporal.extent-discovery", "temporal", context, entitlementKey: "temporal.extent-discovery"),
            Capability("temporal.histogram", "temporal", context, entitlementKey: "temporal.histogram"),
            Capability("temporal.time-series-tiles", "temporal", context, entitlementKey: "temporal.time-series-tiles"),

            Capability("sync.offline", "sync", context, supported: syncSupported, policyCapability: "features.edit", requiresWorkspace: true),
            Capability("realtime.feature-streams", "realtime", context, entitlementKey: "streaming.feature-subscriptions"),
            Capability("alerts.geofence", "alerts", context, entitlementKey: "alerts.enter-exit", configured: alertOptionsValue.Enabled),
            Capability("jobs.runner", "jobs", context, supported: workloadCount > 0 || batchBackends.Any(), requiresAuthentication: true),
            Capability("gitops.release-manifest", "gitops", context, configured: deployTargetCount > 0, policyCapability: "catalog.publish", requiresEnvironment: true),

            Capability("transport.grpc", "transports", context),
            Capability("transport.grpc-web", "transports", context),
            Capability("transport.native-grpc", "transports", context),
            Capability("transport.mcp", "transports", context),
            Capability("transport.qgis", "transports", context),
            Capability("security.mtls", "security", context, configured: mtlsOptions.Mode != ClientCertificateAuthenticationMode.Disabled),

            Capability("preview.file-import", "preview", context, entitlementKey: "import.file", policyCapability: "metadata.write"),
            Capability("query.features", "query", context),
            Capability(
                "analysis.spatial",
                "analysis",
                context,
                entitlementKeys: SpatialAnalyticsEntitlementKeys,
                policyCapability: "features.query"),
            Capability("publication.metadata-release", "publication", context, policyCapability: "catalog.publish", requiresEnvironment: true),
            Capability("upload.file", "upload", context, entitlementKey: "import.file", policyCapability: "metadata.write"),
            Capability("edit.features", "edit", context, policyCapability: "features.edit")
        ];
    }

    private CapabilityManifestCapability Capability(
        string id,
        string category,
        CapabilityPolicyContext context,
        bool supported = true,
        bool configured = true,
        string? entitlementKey = null,
        string[]? entitlementKeys = null,
        string? policyCapability = null,
        bool requiresAuthentication = false,
        bool requiresEnvironment = false,
        bool requiresWorkspace = false)
    {
        var available = true;
        string? reasonCode = null;
        var requiredEntitlementKeys = ResolveRequiredEntitlementKeys(entitlementKey, entitlementKeys);
        var minimumEdition = ResolveMinimumEdition(requiredEntitlementKeys);

        if (!supported)
        {
            available = false;
            reasonCode = CapabilityReasonCodes.Unsupported;
        }
        else if (!configured)
        {
            available = false;
            reasonCode = CapabilityReasonCodes.DisabledByConfiguration;
        }
        else if (requiresEnvironment && !context.EnvironmentAvailable)
        {
            available = false;
            reasonCode = CapabilityReasonCodes.EnvironmentUnavailable;
        }
        else if (requiresWorkspace && string.IsNullOrWhiteSpace(context.WorkspaceId))
        {
            available = false;
            reasonCode = CapabilityReasonCodes.WorkspaceScopeRequired;
        }
        else if (requiresWorkspace && !context.WorkspaceAvailable)
        {
            available = false;
            reasonCode = CapabilityReasonCodes.InsufficientPolicy;
        }
        else if (requiresAuthentication && !context.Authenticated)
        {
            available = false;
            reasonCode = CapabilityReasonCodes.InsufficientPolicy;
        }

        if (available && requiredEntitlementKeys.Length > 0)
        {
            foreach (var requiredEntitlementKey in requiredEntitlementKeys)
            {
                var decision = entitlementService.CheckEntitlement(requiredEntitlementKey);
                if (!decision.IsActive)
                {
                    available = false;
                    reasonCode = ResolveEntitlementReasonCode(context.LicenseSnapshot, decision);
                    break;
                }
            }
        }

        if (available && policyCapability is not null && !HasPolicyCapability(context, policyCapability))
        {
            available = false;
            reasonCode = CapabilityReasonCodes.InsufficientPolicy;
        }

        return new CapabilityManifestCapability
        {
            Id = id,
            Category = category,
            Supported = supported,
            Available = available,
            ReasonCode = available ? null : reasonCode,
            EntitlementKey = entitlementKey,
            EntitlementKeys = entitlementKeys is { Length: > 0 } ? entitlementKeys : null,
            MinimumEdition = minimumEdition,
            MessageKey = available
                ? $"capabilities.{id}.available"
                : $"capabilities.{id}.{reasonCode}"
        };
    }

    private CapabilityManifestTransports BuildTransports()
    {
        var options = clientCertificateOptions.Value;
        var mtlsAvailable = options.Mode != ClientCertificateAuthenticationMode.Disabled;
        return new CapabilityManifestTransports
        {
            MtlsMode = ToWireValue(options.Mode),
            ForwardedClientCertificateEnabled = options.ForwardedCertificate.Enabled,
            Items =
            [
                Transport("rest-http", supported: true, available: true),
                Transport("geoservices-rest", supported: true, available: true),
                Transport("ogc-http", supported: true, available: true),
                Transport("odata", supported: true, available: true),
                Transport("stac", supported: true, available: true),
                Transport("tiles", supported: true, available: true),
                Transport("grpc", supported: true, available: true),
                Transport("grpc-web", supported: true, available: true),
                Transport("native-grpc", supported: true, available: true),
                Transport("mcp", supported: true, available: true),
                Transport("qgis", supported: true, available: true),
                Transport("mtls", supported: true, available: mtlsAvailable,
                    mtlsAvailable ? null : CapabilityReasonCodes.DisabledByConfiguration)
            ]
        };
    }

    private static CapabilityManifestTransportState Transport(
        string id,
        bool supported,
        bool available,
        string? reasonCode = null)
        => new()
        {
            Id = id,
            Supported = supported,
            Available = available,
            ReasonCode = available ? null : reasonCode,
            MessageKey = available
                ? $"transports.{id}.available"
                : $"transports.{id}.{reasonCode}"
        };

    private CapabilityManifestLimits BuildLimits(BatchCapabilitySummary batchCapabilities)
    {
        var limits = limitsOptions.Value;
        var importLimits = limits.Imports;
        var featureStreamOptions = streamOptions.Value;
        var featureChangeEventOptions = eventOptions.Value;
        var uploads = fileUploadOptions.Value;
        var uploadSecurity = fileUploadSecurityOptions.Value;
        var grpc = grpcOptions.Value;
        var analyticsLimits = limits.Analytics;

        return new CapabilityManifestLimits
        {
            Preview = new CapabilityManifestPreviewLimits
            {
                MaxPreviewSizeBytes = importLimits.MaxPreviewSize,
                MaxPreviewFeatures = importLimits.MaxPreviewFeatures,
                MaxPreviewCountScan = importLimits.MaxPreviewCountScan
            },
            Query = new CapabilityManifestQueryLimits
            {
                DefaultRecordCount = limits.Query.DefaultRecordCount,
                MaxRecordCount = limits.Query.MaxRecordCount,
                MaxFeatures = limits.MaxFeatures,
                MaxPageSize = limits.MaxPageSize,
                QueryTimeoutSeconds = (int)Math.Ceiling(limits.Query.QueryTimeout.TotalSeconds),
                MaxBboxAreaSqKm = limits.Query.MaxBboxAreaSqKm,
                MaxFilterDepth = limits.Query.MaxFilterDepth,
                MaxSpatialOperations = limits.Query.MaxSpatialOperations
            },
            Analysis = new CapabilityManifestAnalysisLimits
            {
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxClusters = analyticsLimits.MaxClusters,
                MaxDbscanEpsMeters = analyticsLimits.MaxDbscanEpsMeters,
                MaxKMeansK = analyticsLimits.MaxKMeansK,
                MaxBufferDistanceMeters = analyticsLimits.MaxBufferDistanceMeters,
                MinDensityCellSizeMeters = analyticsLimits.MinDensityCellSizeMeters,
                MaxDensityCellSizeMeters = analyticsLimits.MaxDensityCellSizeMeters,
                MaxDensityCells = analyticsLimits.MaxDensityCells,
                MaxDWithinDistanceMeters = analyticsLimits.MaxDWithinDistanceMeters,
                MaxH3CellsPerQuery = limits.Query.MaxH3CellsPerQuery,
                MaxSpatialOperations = limits.Query.MaxSpatialOperations,
                MaxJoins = limits.Query.MaxJoins
            },
            Publication = new CapabilityManifestPublicationLimits
            {
                ConfiguredDeployTargetCount = controlPlaneOptions.Value.DeployTargets.Count,
                GitOpsManifestExportSupported = true
            },
            Job = new CapabilityManifestJobLimits
            {
                ConfiguredWorkloadCount = controlPlaneOptions.Value.ExecutionWorkloads.Count,
                AvailableBackendCount = batchCapabilities.AvailableBackendCount,
                SupportsCancellation = batchCapabilities.SupportsCancellation,
                SupportsProgressPolling = batchCapabilities.SupportsProgressPolling
            },
            Upload = new CapabilityManifestUploadLimits
            {
                MaxUploadSizeBytes = limits.MaxUploadSizeBytes,
                MaxFileSizeBytes = uploads.MaxFileSizeBytes,
                MaxConcurrentUploads = uploads.MaxConcurrentUploads,
                MaxQueuedUploads = uploads.MaxQueuedUploads,
                MaxSecurityScanSizeBytes = uploadSecurity.MaxSecurityScanSizeBytes
            },
            Streaming = new CapabilityManifestStreamingLimits
            {
                MaxConcurrentSessions = featureStreamOptions.MaxConcurrentSessions,
                MaxBufferPerConnection = featureStreamOptions.MaxBufferPerConnection,
                MaxSubscriptionsPerSession = featureStreamOptions.MaxSubscriptionsPerSession,
                MaxSubscriptionIdLength = featureStreamOptions.MaxSubscriptionIdLength,
                MaxControlFrameBytes = featureStreamOptions.MaxControlFrameBytes,
                CursorRetentionLimit = featureChangeEventOptions.MaxRetainedEvents,
                HeartbeatIntervalSeconds = featureStreamOptions.HeartbeatInterval.TotalSeconds,
                GrpcStreamBatchSize = Math.Max(grpc.StreamBatchSize, 1)
            },
            Edit = new CapabilityManifestEditLimits
            {
                MaxFeaturesPerEdit = limits.Edits.MaxFeaturesPerEdit,
                MaxEditsPerTransaction = limits.Edits.MaxEditsPerTransaction,
                MaxPayloadSizeBytes = limits.Edits.MaxPayloadSize
            },
            Geometry = new CapabilityManifestGeometryLimits
            {
                MaxVerticesPerGeometry = limits.Geometry.MaxVerticesPerGeometry,
                MaxGeometrySizeBytes = limits.Geometry.MaxGeometrySize,
                MaxCoordinatePrecision = limits.Geometry.MaxCoordinatePrecision
            },
            Attachment = new CapabilityManifestAttachmentLimits
            {
                MaxAttachmentsPerFeature = limits.Attachments.MaxAttachmentsPerFeature,
                MaxAttachmentSizeBytes = limits.Attachments.MaxAttachmentSize
            }
        };
    }

    private CapabilityManifestPolicies BuildPolicies(
        LicenseSnapshot snapshot,
        IReadOnlyList<string> callerCapabilities)
    {
        var entitlements = FeatureCatalog.All
            .Select(feature =>
            {
                var decision = entitlementService.CheckEntitlement(feature.Key);
                return new CapabilityManifestEntitlementDecision
                {
                    Key = feature.Key,
                    Active = decision.IsActive,
                    MinimumEdition = feature.MinimumEdition.ToString(),
                    ReasonCode = decision.IsActive ? null : ResolveEntitlementReasonCode(snapshot, decision)
                };
            })
            .ToArray();

        return new CapabilityManifestPolicies
        {
            CurrentEdition = snapshot.Edition.ToString(),
            LicenseValidationState = snapshot.ValidationState.ToString(),
            LicenseValid = snapshot.IsValid,
            CallerCapabilities = callerCapabilities
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Entitlements = entitlements,
            AuthorizationNotice = AuthorizationNotice
        };
    }

    private static CapabilityManifestLink[] BuildLinks()
        =>
        [
            new CapabilityManifestLink
            {
                Rel = "self",
                Href = "/api/v1/capabilities/manifest",
                Type = CapabilityManifestConstants.ManifestContentType
            },
            new CapabilityManifestLink
            {
                Rel = "feature-streaming-capabilities",
                Href = "/api/v1/streaming/features/capabilities",
                Type = CapabilityManifestConstants.JsonContentType
            },
            new CapabilityManifestLink
            {
                Rel = "admin-capabilities",
                Href = "/api/v1/admin/capabilities",
                Type = CapabilityManifestConstants.JsonContentType
            }
        ];

    private async Task<BatchCapabilitySummary> ResolveBatchCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var backends = batchBackends.ToArray();
        if (backends.Length == 0)
        {
            return new BatchCapabilitySummary(0, false, false);
        }

        var available = 0;
        var supportsCancellation = false;
        var supportsProgressPolling = false;
        foreach (var backend in backends)
        {
            try
            {
                var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
                available++;
                supportsCancellation |= capabilities.SupportsCancellation;
                supportsProgressPolling |= capabilities.SupportsProgressPolling;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                CapabilityManifestLog.BatchCapabilityProbeFailed(logger, backend.BackendName, ex);
            }
        }

        return new BatchCapabilitySummary(available, supportsCancellation, supportsProgressPolling);
    }

    private bool IsFieldCollectionSyncSupported()
    {
        var serviceInspector = serviceProvider.GetService<IServiceProviderIsService>();
        return serviceInspector?.IsService(typeof(IFieldCollectionSyncStore)) == true ||
            serviceProvider.GetService<IFieldCollectionSyncStore>() is not null;
    }

    private bool ResolveWorkspaceAvailability(
        ClaimsPrincipal principal,
        string? workspaceId,
        bool authenticated)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return true;
        }

        if (!authenticated)
        {
            return false;
        }

        if (HasAdminRole(principal))
        {
            return true;
        }

        var workspaceClaimType = rbacOptions.Value.WorkspaceScopeClaimType;
        return principal.Claims.Any(claim =>
            string.Equals(claim.Type, workspaceClaimType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, workspaceId, StringComparison.Ordinal));
    }

    private bool HasAdminRole(ClaimsPrincipal principal)
    {
        var roleClaimType = rbacOptions.Value.EffectiveRoleClaimType;
        var checkStandardRoleClaim = !string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase);

        foreach (var claim in principal.Claims)
        {
            var isRoleClaim = string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase)
                || (checkStandardRoleClaim && string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase));
            if (isRoleClaim && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPolicyCapability(CapabilityPolicyContext context, string capability)
    {
        if (!context.Authenticated)
        {
            return false;
        }

        return context.CallerCapabilities.Contains(capability);
    }

    private static string[] ResolveRequiredEntitlementKeys(string? entitlementKey, string[]? entitlementKeys)
    {
        if (entitlementKeys is { Length: > 0 })
        {
            return entitlementKeys;
        }

        return entitlementKey is null ? [] : [entitlementKey];
    }

    private static string? ResolveMinimumEdition(string[] entitlementKeys)
    {
        HonuaEdition? minimumEdition = null;
        foreach (var entitlementKey in entitlementKeys)
        {
            var feature = FeatureCatalog.All.FirstOrDefault(item =>
                string.Equals(item.Key, entitlementKey, StringComparison.OrdinalIgnoreCase));
            if (feature is null)
            {
                continue;
            }

            if (!minimumEdition.HasValue || feature.MinimumEdition > minimumEdition.Value)
            {
                minimumEdition = feature.MinimumEdition;
            }
        }

        return minimumEdition?.ToString();
    }

    private static string ResolveEntitlementReasonCode(
        LicenseSnapshot snapshot,
        LicenseEntitlementDecision decision)
    {
        if (snapshot.ValidationState != LicenseValidationState.Valid)
        {
            return CapabilityReasonCodes.LicenseRequired;
        }

        if (decision.RequiredEdition.HasValue && snapshot.Edition < decision.RequiredEdition.Value)
        {
            return CapabilityReasonCodes.LicenseRequired;
        }

        return CapabilityReasonCodes.EntitlementInactive;
    }

    private static string ToWireValue(MetadataV2StorageType value)
        => value switch
        {
            MetadataV2StorageType.RelationalTable => "relational-table",
            MetadataV2StorageType.SqlView => "sql-view",
            MetadataV2StorageType.SqlQuery => "sql-query",
            MetadataV2StorageType.GeoPackageTable => "geopackage-table",
            MetadataV2StorageType.GeoJson => "geojson",
            MetadataV2StorageType.GeoParquet => "geoparquet",
            MetadataV2StorageType.Arrow => "arrow",
            MetadataV2StorageType.CloudOptimizedGeoTiff => "cloud-optimized-geotiff",
            MetadataV2StorageType.Zarr => "zarr",
            MetadataV2StorageType.NetCdf => "netcdf",
            MetadataV2StorageType.MbTiles => "mbtiles",
            MetadataV2StorageType.PmTiles => "pmtiles",
            MetadataV2StorageType.TileCache => "tile-cache",
            MetadataV2StorageType.ObjectPrefix => "object-prefix",
            MetadataV2StorageType.ExternalApi => "external-api",
            MetadataV2StorageType.StacAsset => "stac-asset",
            _ => value.ToString()
        };

    private static string ToWireValue(MetadataV2PublicationType value)
        => value switch
        {
            MetadataV2PublicationType.OgcCollection => "ogc-collection",
            MetadataV2PublicationType.WfsFeatureType => "wfs-feature-type",
            MetadataV2PublicationType.WmsLayer => "wms-layer",
            MetadataV2PublicationType.WmtsLayer => "wmts-layer",
            MetadataV2PublicationType.EsriFeatureLayer => "esri-feature-layer",
            MetadataV2PublicationType.EsriMapLayer => "esri-map-layer",
            MetadataV2PublicationType.EsriImageLayer => "esri-image-layer",
            MetadataV2PublicationType.StacCollection => "stac-collection",
            MetadataV2PublicationType.DcatDistribution => "dcat-distribution",
            MetadataV2PublicationType.OgcRecord => "ogc-record",
            MetadataV2PublicationType.ODataEntitySet => "odata-entity-set",
            MetadataV2PublicationType.Custom => "custom",
            _ => value.ToString()
        };

    private static string ToWireValue(ClientCertificateAuthenticationMode mode)
        => mode switch
        {
            ClientCertificateAuthenticationMode.Disabled => "disabled",
            ClientCertificateAuthenticationMode.Optional => "optional",
            ClientCertificateAuthenticationMode.RequiredForNative => "required-for-native",
            ClientCertificateAuthenticationMode.RequiredForAdmin => "required-for-admin",
            ClientCertificateAuthenticationMode.RequiredForEnvironment => "required-for-environment",
            _ => mode.ToString()
        };

    private sealed record CapabilityPolicyContext(
        LicenseSnapshot LicenseSnapshot,
        HashSet<string> CallerCapabilities,
        bool Authenticated,
        bool EnvironmentAvailable,
        bool WorkspaceAvailable,
        string? WorkspaceId);

    private readonly record struct BatchCapabilitySummary(
        int AvailableBackendCount,
        bool SupportsCancellation,
        bool SupportsProgressPolling);
}

internal static class CapabilityReasonCodes
{
    public const string Unsupported = "unsupported";
    public const string DisabledByConfiguration = "disabled-by-configuration";
    public const string LicenseRequired = "license-required";
    public const string EntitlementInactive = "entitlement-inactive";
    public const string InsufficientPolicy = "insufficient-policy";
    public const string EnvironmentUnavailable = "environment-unavailable";
    public const string WorkspaceScopeRequired = "workspace-scope-required";
}

internal static partial class CapabilityManifestLog
{
    [LoggerMessage(
        EventId = 8720,
        Level = LogLevel.Information,
        Message = "Capability manifest generated. TenantSource={TenantSource} EnvironmentRequested={EnvironmentRequested} WorkspaceRequested={WorkspaceRequested} Authenticated={Authenticated} CapabilityCount={CapabilityCount} UnavailableCount={UnavailableCount}")]
    public static partial void ManifestGenerated(
        ILogger logger,
        string tenantSource,
        bool environmentRequested,
        bool workspaceRequested,
        bool authenticated,
        int capabilityCount,
        int unavailableCount);

    [LoggerMessage(
        EventId = 8721,
        Level = LogLevel.Warning,
        Message = "Capability manifest environment snapshot lookup failed. Environment={Environment}")]
    public static partial void EnvironmentSnapshotFailed(
        ILogger logger,
        string environment,
        Exception exception);

    [LoggerMessage(
        EventId = 8722,
        Level = LogLevel.Warning,
        Message = "Capability manifest batch backend capability probe failed. Backend={Backend}")]
    public static partial void BatchCapabilityProbeFailed(
        ILogger logger,
        string backend,
        Exception exception);
}
