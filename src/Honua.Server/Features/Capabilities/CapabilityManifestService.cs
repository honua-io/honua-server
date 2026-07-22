// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Honua.Core.Configuration;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Capabilities;
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
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.ControlPlane;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Server.Features.Streaming;
using Honua.ServiceDefaults;

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
    CapabilityManifestOptionsSnapshot options,
    ILicenseEntitlementService entitlementService,
    IConsoleActionEvaluator consoleActionEvaluator,
    IMetadataV2EnvironmentSnapshotReader environmentSnapshotReader,
    IEnumerable<IBatchComputeBackend> batchBackends,
    IEnumerable<IFieldCollectionSyncStore> fieldCollectionSyncStores,
    IWebHostEnvironment hostEnvironment,
    ICapabilityRegistry capabilityRegistry,
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

        // #2335 (B3): the registry-derived composition resolves each descriptor through
        // the shared gate resolver (edition/experimental precedence). All descriptors
        // stay Implemented today, so this produces the same wire document as the legacy
        // hand-curated composition; the gate context is the seam T10 (#2346) flips.
        var gateContext = BuildGateContext(snapshot.Edition, request.Environment);

        var capabilities = options.ManifestFromRegistry
            ? BuildCapabilitiesFromRegistry(policyContext, gateContext)
            : BuildCapabilities(policyContext);
        var packages = options.ManifestFromRegistry
            ? BuildPackagesFromRegistry(gateContext)
            : BuildPackages();
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
            Packages = packages,
            Capabilities = capabilities,
            Transports = BuildTransports(),
            Limits = BuildLimits(batchCapabilities),
            Policies = BuildPolicies(snapshot, callerCapabilities),
            DeploymentProfile = options.DeploymentProfile,
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: best-effort environment snapshot lookup for the
            // capability manifest; any failure is reported as an unavailable environment
            // entry below rather than failing the whole manifest response.
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
            SchemaVersions = BuildPackageSchemaVersions(),
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
            StorageFamilies = BuildStorageFamilies(),
            PublicationFamilies = BuildPublicationFamilies()
        };
    }

    /// <summary>
    /// The #2335 (B3) registry-derived <c>Packages</c> block: the <c>Families[]</c> are
    /// derived by iterating the <c>packages</c>-category descriptors of the unified
    /// capability registry through the gate resolver. The wire family id/kind come from
    /// <see cref="PackageWireById"/> and the schema version from the descriptor's
    /// <see cref="CapabilityDescriptor.PackageSchemaVersion"/> (the null seam B1 left);
    /// an experimental-disabled family is omitted (wire-compatible). The schema-version,
    /// storage-family and publication-family lists are identical to the legacy path.
    /// </summary>
    private CapabilityManifestPackages BuildPackagesFromRegistry(CapabilityGateContext gateContext)
    {
        var families = new List<CapabilityManifestPackageFamily>();
        foreach (var descriptor in capabilityRegistry.All)
        {
            if (!PackageWireById.TryGetValue(descriptor.Id, out var wire))
            {
                continue;
            }

            var resolution = CapabilityGateResolver.Resolve(descriptor, gateContext);
            if (IsExperimentalDisabled(resolution))
            {
                // Experimental-and-flag-off package family: omit it (wire-compatible).
                continue;
            }

            families.Add(new CapabilityManifestPackageFamily
            {
                Id = wire.Id,
                Kind = wire.Kind,
                SchemaVersion = descriptor.PackageSchemaVersion
                    ?? throw new InvalidOperationException(
                        $"Package capability '{descriptor.Id}' is missing a PackageSchemaVersion."),
                Supported = resolution.Enabled
            });
        }

        return new CapabilityManifestPackages
        {
            SchemaVersions = BuildPackageSchemaVersions(),
            Families = families.ToArray(),
            StorageFamilies = BuildStorageFamilies(),
            PublicationFamilies = BuildPublicationFamilies()
        };
    }

    private static string[] BuildPackageSchemaVersions()
        =>
        [
            CapabilityManifestConstants.SchemaVersion,
            MetadataV2Constants.ApiVersion,
            MetadataV2Constants.SchemaVersion,
            "honua_map_package.v1",
            "honua_app_package.v1"
        ];

    private static string[] BuildStorageFamilies()
        => Enum.GetValues<MetadataV2StorageType>()
            .Select(ToWireValue)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] BuildPublicationFamilies()
        => Enum.GetValues<MetadataV2PublicationType>()
            .Select(ToWireValue)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Maps each <c>packages</c>-category registry descriptor id to the wire family
    /// id/kind the <c>honua.capability_manifest.v1</c> surface advertises. The iteration
    /// order of <see cref="ICapabilityRegistry.All"/> preserves the legacy family order.
    /// </summary>
    private static readonly Dictionary<string, (string Id, string Kind)> PackageWireById =
        new Dictionary<string, (string Id, string Kind)>(StringComparer.Ordinal)
        {
            ["package.metadata-v2"] = ("metadata-v2-graph", "metadata"),
            ["package.release-package"] = ("metadata-release-package", "publication"),
            ["package.gitops-manifest"] = ("gitops-metadata-release-manifest", "gitops"),
            ["package.map"] = ("map-package", "map"),
            ["package.app"] = ("app-package", "app"),
        };

    private CapabilityManifestCapability[] BuildCapabilities(CapabilityPolicyContext context)
    {
        var alertOptionsValue = options.Alerts;
        var mtlsOptions = options.ClientCertificate;
        var syncSupported = IsFieldCollectionSyncSupported();
        var deployTargetCount = options.ControlPlane.DeployTargets.Count;
        var workloadCount = options.ControlPlane.ExecutionWorkloads.Count;

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

            Capability("sync.offline", "sync", context, supported: syncSupported, entitlementKey: FeatureCatalog.FieldOpsOfflineSyncKey, policyCapability: "features.edit", requiresWorkspace: true),
            Capability("realtime.feature-streams", "realtime", context, entitlementKey: "streaming.feature-subscriptions"),
            Capability("alerts.geofence", "alerts", context, entitlementKey: "alerts.enter-exit", configured: alertOptionsValue.Enabled),
            Capability("jobs.runner", "jobs", context, supported: workloadCount > 0 || batchBackends.Any(), requiresAuthentication: true),
            Capability("ai.spec-apply", "ai", context, entitlementKey: FeatureCatalog.AiSpecApplyKey),
            Capability("ai.grounding", "ai", context, entitlementKey: FeatureCatalog.AiGroundingKey),
            Capability("ai.workflow-generation", "ai", context, entitlementKey: FeatureCatalog.AiWorkflowGenerationKey),
            Capability("gitops.release-manifest", "gitops", context, configured: deployTargetCount > 0, policyCapability: "catalog.publish", requiresEnvironment: true),

            Capability("transport.grpc", "transports", context),
            Capability("transport.grpc-web", "transports", context),
            Capability("transport.native-grpc", "transports", context),
            Capability("transport.mcp", "transports", context),
            Capability("transport.qgis", "transports", context),
            Capability("security.mtls", "security", context, entitlementKey: FeatureCatalog.MtlsClientCertificateKey, configured: mtlsOptions.Mode != ClientCertificateAuthenticationMode.Disabled),

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
            Capability("edit.features", "edit", context, entitlementKey: FeatureCatalog.FeatureServerEditsKey, policyCapability: "features.edit"),
            // Branch versioning (VMS) — built-experimental, gated OFF the GA surface by
            // default (#2480 / ADR-0058). Mirrors the registry descriptor order
            // (CapabilityRegistry.BuildManifestCapabilityDescriptors) so the hand-curated and
            // registry-derived Capabilities[] stay byte-identical.
            Capability("versioning.branch", "versioning", context, entitlementKey: FeatureCatalog.BranchVersioningKey),
            // Aggregated operate status (A12) — the server-authoritative operate/status surface. Ungated
            // GA; read-authorized (ops:read) at the HTTP layer. Kept last to mirror the registry order.
            Capability("operate.status", "operate", context, requiresAuthentication: true)
        ];
    }

    /// <summary>
    /// The #2335 (B3) registry-derived <c>Capabilities[]</c>. Instead of the
    /// hand-curated roster in <see cref="BuildCapabilities"/>, this iterates the unified
    /// capability registry's manifest descriptors (in their frozen order), resolves each
    /// through the shared <see cref="CapabilityGateResolver"/>, and omits any descriptor
    /// the gate turns off as experimental-and-flag-off (wire-compatible — the entry is
    /// simply absent). The per-capability config knobs the registry does not carry
    /// (supported/configured/requires*/policy/composite entitlement keys) come from
    /// <see cref="BuildManifestCapabilitySpecs"/>; the availability computation is the
    /// same <see cref="Capability"/> factory, so while every descriptor stays
    /// <see cref="CapabilityMaturity.Implemented"/> the wire document is unchanged.
    /// </summary>
    private CapabilityManifestCapability[] BuildCapabilitiesFromRegistry(
        CapabilityPolicyContext context,
        CapabilityGateContext gateContext)
    {
        var specs = BuildManifestCapabilitySpecs();
        var capabilities = new List<CapabilityManifestCapability>(specs.Count);
        foreach (var descriptor in capabilityRegistry.All)
        {
            if (!IsManifestCapability(descriptor))
            {
                continue;
            }

            var resolution = CapabilityGateResolver.Resolve(descriptor, gateContext);
            if (IsExperimentalDisabled(resolution))
            {
                continue;
            }

            var spec = specs.GetValueOrDefault(descriptor.Id, ManifestCapabilitySpec.Default);
            capabilities.Add(Capability(
                descriptor.Id,
                descriptor.Category,
                context,
                supported: spec.Supported,
                configured: spec.Configured,
                entitlementKey: spec.EntitlementKey,
                entitlementKeys: spec.EntitlementKeys,
                policyCapability: spec.PolicyCapability,
                requiresAuthentication: spec.RequiresAuthentication,
                requiresEnvironment: spec.RequiresEnvironment,
                requiresWorkspace: spec.RequiresWorkspace));
        }

        return capabilities.ToArray();
    }

    /// <summary>
    /// The per-capability config knobs the registry descriptor does not carry, keyed by
    /// descriptor id. Ids absent from the map use <see cref="ManifestCapabilitySpec.Default"/>.
    /// Mirrors the hand-curated arguments in <see cref="BuildCapabilities"/> exactly so the
    /// two composition paths stay wire-identical.
    /// </summary>
    private Dictionary<string, ManifestCapabilitySpec> BuildManifestCapabilitySpecs()
    {
        var syncSupported = IsFieldCollectionSyncSupported();
        var jobsSupported = options.ControlPlane.ExecutionWorkloads.Count > 0 || batchBackends.Any();
        var alertsConfigured = options.Alerts.Enabled;
        var gitopsConfigured = options.ControlPlane.DeployTargets.Count > 0;
        var mtlsConfigured = options.ClientCertificate.Mode != ClientCertificateAuthenticationMode.Disabled;

        return new Dictionary<string, ManifestCapabilitySpec>(StringComparer.Ordinal)
        {
            ["package.release-package"] = new() { PolicyCapability = "catalog.publish", RequiresEnvironment = true },
            ["package.gitops-manifest"] = new() { PolicyCapability = "catalog.publish", RequiresEnvironment = true },
            ["package.map"] = new() { PolicyCapability = "studio.edit" },
            ["package.app"] = new() { PolicyCapability = "studio.edit" },

            ["temporal.filtering"] = new() { EntitlementKey = "temporal.filtering" },
            ["temporal.extent-discovery"] = new() { EntitlementKey = "temporal.extent-discovery" },
            ["temporal.histogram"] = new() { EntitlementKey = "temporal.histogram" },
            ["temporal.time-series-tiles"] = new() { EntitlementKey = "temporal.time-series-tiles" },

            ["sync.offline"] = new()
            {
                Supported = syncSupported,
                EntitlementKey = FeatureCatalog.FieldOpsOfflineSyncKey,
                PolicyCapability = "features.edit",
                RequiresWorkspace = true,
            },
            ["realtime.feature-streams"] = new() { EntitlementKey = "streaming.feature-subscriptions" },
            ["alerts.geofence"] = new() { EntitlementKey = "alerts.enter-exit", Configured = alertsConfigured },
            ["jobs.runner"] = new() { Supported = jobsSupported, RequiresAuthentication = true },
            ["ai.spec-apply"] = new() { EntitlementKey = FeatureCatalog.AiSpecApplyKey },
            ["ai.grounding"] = new() { EntitlementKey = FeatureCatalog.AiGroundingKey },
            ["ai.workflow-generation"] = new() { EntitlementKey = FeatureCatalog.AiWorkflowGenerationKey },
            ["gitops.release-manifest"] = new()
            {
                Configured = gitopsConfigured,
                PolicyCapability = "catalog.publish",
                RequiresEnvironment = true,
            },

            ["security.mtls"] = new() { Configured = mtlsConfigured, EntitlementKey = FeatureCatalog.MtlsClientCertificateKey },
            ["preview.file-import"] = new() { EntitlementKey = "import.file", PolicyCapability = "metadata.write" },
            ["analysis.spatial"] = new()
            {
                EntitlementKeys = SpatialAnalyticsEntitlementKeys,
                PolicyCapability = "features.query",
            },
            ["publication.metadata-release"] = new() { PolicyCapability = "catalog.publish", RequiresEnvironment = true },
            ["upload.file"] = new() { EntitlementKey = "import.file", PolicyCapability = "metadata.write" },
            ["edit.features"] = new() { EntitlementKey = FeatureCatalog.FeatureServerEditsKey, PolicyCapability = "features.edit" },
            ["versioning.branch"] = new() { EntitlementKey = FeatureCatalog.BranchVersioningKey },
            ["operate.status"] = new() { RequiresAuthentication = true },
        };
    }

    private CapabilityGateContext BuildGateContext(HonuaEdition edition, string? environment)
        => new()
        {
            Edition = edition,
            DeploymentEnvironment = string.IsNullOrWhiteSpace(environment) ? null : environment,
            ExperimentalFlags = options.ExperimentalCapabilityFlags,
        };

    /// <summary>
    /// A registry descriptor participates in the #1186 manifest <c>Capabilities[]</c>
    /// when it is neither an <c>/mcp</c> tool/resource nor a data-format descriptor —
    /// those roster kinds project onto other surfaces, not the manifest.
    /// </summary>
    private static bool IsManifestCapability(CapabilityDescriptor descriptor)
        => !descriptor.Id.StartsWith(CapabilityRegistry.McpToolIdPrefix, StringComparison.Ordinal)
            && !descriptor.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal)
            && !descriptor.Id.StartsWith(CapabilityRegistry.DataFormatIdPrefix, StringComparison.Ordinal);

    private static bool IsExperimentalDisabled(CapabilityResolution resolution)
        => !resolution.Enabled
            && string.Equals(
                resolution.ReasonCode,
                Core.Features.Capabilities.CapabilityReasonCodes.ExperimentalDisabled,
                StringComparison.Ordinal);

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
        var clientCertificate = options.ClientCertificate;
        var mtlsAvailable = clientCertificate.Mode != ClientCertificateAuthenticationMode.Disabled;
        return new CapabilityManifestTransports
        {
            MtlsMode = ToWireValue(clientCertificate.Mode),
            ForwardedClientCertificateEnabled = clientCertificate.ForwardedCertificate.Enabled,
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
        var limits = options.Limits;
        var importLimits = limits.Imports;
        var featureStreamOptions = options.Streaming;
        var featureChangeEventOptions = options.FeatureChangeEvents;
        var uploads = options.FileUpload;
        var uploadSecurity = options.FileUploadSecurity;
        var grpc = options.Grpc;
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
                ConfiguredDeployTargetCount = options.ControlPlane.DeployTargets.Count,
                GitOpsManifestExportSupported = true
            },
            Job = new CapabilityManifestJobLimits
            {
                ConfiguredWorkloadCount = options.ControlPlane.ExecutionWorkloads.Count,
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional broad catch: per-backend loop probing capabilities; one
                // backend's failure must not abort probing the remaining backends.
                CapabilityManifestLog.BatchCapabilityProbeFailed(logger, backend.BackendName, ex);
            }
        }

        return new BatchCapabilitySummary(available, supportsCancellation, supportsProgressPolling);
    }

    private bool IsFieldCollectionSyncSupported()
        => fieldCollectionSyncStores.Any();

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

        var workspaceClaimType = options.Rbac.WorkspaceScopeClaimType;
        return principal.Claims.Any(claim =>
            string.Equals(claim.Type, workspaceClaimType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, workspaceId, StringComparison.Ordinal));
    }

    private bool HasAdminRole(ClaimsPrincipal principal)
    {
        var roleClaimType = options.Rbac.EffectiveRoleClaimType;
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
            MetadataV2PublicationType.EsriVectorTileLayer => "esri-vector-tile-layer",
            MetadataV2PublicationType.StacCollection => "stac-collection",
            MetadataV2PublicationType.DcatDistribution => "dcat-distribution",
            MetadataV2PublicationType.OgcRecord => "ogc-record",
            MetadataV2PublicationType.ODataEntitySet => "odata-entity-set",
            MetadataV2PublicationType.SensorThingsDatastream => "sensorthings-datastream",
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

    /// <summary>
    /// The manifest-specific config knobs a registry descriptor does not carry, layered
    /// onto the registry roster by <see cref="BuildCapabilitiesFromRegistry"/> (#2335 / B3).
    /// </summary>
    private sealed record ManifestCapabilitySpec
    {
        public static ManifestCapabilitySpec Default { get; } = new();

        public bool Supported { get; init; } = true;

        public bool Configured { get; init; } = true;

        public string? EntitlementKey { get; init; }

        public string[]? EntitlementKeys { get; init; }

        public string? PolicyCapability { get; init; }

        public bool RequiresAuthentication { get; init; }

        public bool RequiresEnvironment { get; init; }

        public bool RequiresWorkspace { get; init; }
    }

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
