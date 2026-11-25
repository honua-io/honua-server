// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class AdminPlatformScopeGuardTests
{
    private const string FeaturesRelativePath = "src/Honua.Server/Features";

    private static readonly Regex AdminRoutePolicyRegex = new(
        @"\b(RequireAdminAuthorization|RequireOpsReadAuthorization)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteMappingRegex = new(
        @"\.(MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods|MapGroup)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlatformGuardMarkerRegex = new(
        @"\b(RequirePlatformAdminAuthority|RequirePlatformAdminAuthorization|AlertAdminIsolationFilter|PlatformDeployAuthority)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HostGlobalSurfaceRegex = new(
        @"\b(TenantIndependentControlPlaneMetadata|ISecureConnectionRegistry|IRoleStore|IRlsPolicyStore|IFieldMaskPolicyStore|IUserStore|IRateLimitPolicyStore|ILicenseService|IOidcProviderStore|IClientCertificateStore|ITenantRegistry|IAdminApiKeyStore|IOAuthClientStore|IOpsAutonomyStore|IGpProcessReleaseStore|IInvestigationStore|RealtimeAlertNotificationBroadcaster)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Explicitly reviewed read-only, tenant-scoped, or non-store diagnostic admin surfaces that
    // intentionally do not require platform-admin authority.
    private static readonly string[] ReviewedExceptions =
    [
        // Read-only /config documentation and anonymous /openapi.json; connection table discovery is tenant-scoped.
        "src/Honua.Server/Features/Admin/AdminEndpoints.cs",
        // Read-only /version and anonymous /capabilities build-metadata handshake.
        "src/Honua.Server/Features/Admin/AdminInfoEndpoints.cs",
        // Stateless outbound URL discovery probe; accesses no host-global store.
        "src/Honua.Server/Features/Admin/ExternalServiceDiscoveryEndpoints.cs",
        // Read-only edition feature and capability catalog overview.
        "src/Honua.Server/Features/Admin/FeatureOverviewEndpoints.cs",
        // Tenant-scoped layer publishing; uses ISecureConnectionRegistry only for read-only name-to-id resolution.
        "src/Honua.Server/Features/Admin/LayerPublishingEndpoints.cs",
        // Liveness/readiness/deep health probes and read-only /healthz/metrics.
        "src/Honua.Server/Features/HealthCheck/HealthEndpoints.cs",
        // Sanitized mobile exception telemetry logging only; accesses no host-global store.
        "src/Honua.Server/Features/Mobile/Diagnostics/MobileExceptionIngestionEndpoints.cs",
    ];

    [ArchitectureTest]
    public void HostGlobalAdminEndpoints_RequirePlatformAdminScope()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = ArchitectureTestHelpers.CombinePath(repoRoot, FeaturesRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(featuresRoot).Should().BeTrue($"expected features directory at {FeaturesRelativePath}");

        var (guardedCount, unguarded) = ScanEndpointFiles(repoRoot, featuresRoot);
        var reviewedSet = new HashSet<string>(ReviewedExceptions, StringComparer.Ordinal);
        var gaps = unguarded.Where(path => !reviewedSet.Contains(path)).ToList();
        var stale = ReviewedExceptions.Where(path => !unguarded.Contains(path, StringComparer.Ordinal)).ToList();

        guardedCount.Should().BeGreaterThan(20,
            "the platform-admin scope guard should cover the host-global admin endpoints in src/Honua.Server/Features");
        stale.Should().BeEmpty(
            "reviewed exceptions must still match an unguarded host-global admin endpoint; remove stale entries: "
            + string.Join("; ", stale));
        gaps.Should().BeEmpty(
            "every admin or ops-read endpoint file referencing TenantIndependentControlPlaneMetadata or a host-global "
            + "store must explicitly declare RequirePlatformAdminAuthority, RequirePlatformAdminAuthorization, "
            + "AlertAdminIsolationFilter, or PlatformDeployAuthority. Unguarded files: "
            + string.Join("; ", gaps));
    }

    private static (int GuardedCount, List<string> Unguarded) ScanEndpointFiles(string repoRoot, string featuresRoot)
    {
        var guardedCount = 0;
        var unguarded = new List<string>();

        foreach (var file in Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (IsBuildOutput(relativePath))
            {
                continue;
            }

            var content = StripComments(File.ReadAllText(file));
            if (!RouteMappingRegex.IsMatch(content) || !AdminRoutePolicyRegex.IsMatch(content) || !HostGlobalSurfaceRegex.IsMatch(content))
            {
                continue;
            }

            if (PlatformGuardMarkerRegex.IsMatch(content))
            {
                guardedCount++;
                continue;
            }

            unguarded.Add(relativePath);
        }

        unguarded.Sort(StringComparer.Ordinal);
        return (guardedCount, unguarded);
    }

    private static bool IsBuildOutput(string relativePath)
        => relativePath.Contains("/bin/", StringComparison.Ordinal)
            || relativePath.Contains("/obj/", StringComparison.Ordinal);

    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, "//[^\n]*", string.Empty);
    }
}
