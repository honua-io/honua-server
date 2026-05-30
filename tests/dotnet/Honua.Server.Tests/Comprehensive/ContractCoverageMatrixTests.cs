// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Comprehensive;

/// <summary>
/// Validates that the endpoint-to-scenario coverage matrix for Admin, Geocoding, and
/// Geometry Service areas meets the minimum depth requirement: each endpoint has at
/// least a happy-path and one meaningful negative-path test.
/// </summary>
/// <remarks>
/// <para>Endpoint-to-Scenario Matrix (Admin):</para>
/// <list type="table">
/// <listheader><term>Endpoint</term><description>Scenarios</description></listheader>
/// <item><term>GET  /api/v1/admin/config</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/openapi.json</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/version</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/capabilities</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/services</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/services/{name}/settings</term><description>happy, 401</description></item>
/// <item><term>PUT  /api/v1/admin/services/{name}/protocols</term><description>happy, 401</description></item>
/// <item><term>PUT  /api/v1/admin/services/{name}/access-policy</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/observability/errors</term><description>happy (empty), 401</description></item>
/// <item><term>GET  /api/v1/admin/observability/telemetry</term><description>happy, disabled, 401</description></item>
/// <item><term>GET  /api/v1/admin/observability/migrations</term><description>happy (running), plan-fail, 401</description></item>
/// <item><term>GET  /api/v1/admin/deploy/preflight</term><description>ready, blocked, 401</description></item>
/// <item><term>POST /api/v1/admin/deploy/plan</term><description>happy, bad-target, approval-gated, 401</description></item>
/// <item><term>POST /api/v1/admin/deploy/operations</term><description>happy, submit-immediately, approval-gated, 401</description></item>
/// <item><term>GET  /api/v1/admin/deploy/operations/{id}</term><description>happy, 404, reconcile, 401</description></item>
/// <item><term>POST /api/v1/admin/deploy/operations/{id}/submit</term><description>happy, double-submit</description></item>
/// <item><term>POST /api/v1/admin/deploy/operations/{id}/rollback</term><description>happy, 404, approval-denied</description></item>
/// <item><term>GET  /api/v1/admin/connections</term><description>happy, 401</description></item>
/// <item><term>POST /api/v1/admin/connections</term><description>happy (encrypted, secret-ref), 400, duplicate, 401</description></item>
/// <item><term>GET  /api/v1/admin/connections/{id}</term><description>happy, 404</description></item>
/// <item><term>PUT  /api/v1/admin/connections/{id}</term><description>happy, bad-ssl</description></item>
/// <item><term>DELETE /api/v1/admin/connections/{id}</term><description>happy</description></item>
/// <item><term>POST /api/v1/admin/connections/{id}/test</term><description>happy</description></item>
/// <item><term>POST /api/v1/admin/connections/test</term><description>happy, bad-ssl, 401</description></item>
/// <item><term>POST /api/v1/admin/connections/encryption/validate</term><description>happy, 401</description></item>
/// <item><term>POST /api/v1/admin/connections/encryption/rotate-key</term><description>bad-request, 401</description></item>
/// <item><term>GET  /api/v1/admin/connections/{id}/layers</term><description>happy, 404, 401</description></item>
/// <item><term>POST /api/v1/admin/connections/{id}/layers</term><description>happy, 400 (missing/pk), duplicate, approval-denied, 401</description></item>
/// <item><term>PUT  /api/v1/admin/connections/{id}/layers/{lid}/enabled</term><description>happy, 404</description></item>
/// <item><term>PUT  /api/v1/admin/connections/{id}/layers/enabled</term><description>happy (bulk)</description></item>
/// <item><term>GET  /api/v1/admin/metadata/layers/{lid}/style</term><description>happy, 401</description></item>
/// <item><term>PUT  /api/v1/admin/metadata/layers/{lid}/style</term><description>happy, 401</description></item>
/// <item><term>GET  /api/v1/admin/operations/{id}</term><description>happy, 404, 401</description></item>
/// <item><term>POST /api/v1/admin/operations/{id}/cancel</term><description>happy, already-cancelled, completed</description></item>
/// <item><term>GET  /api/v1/admin/operations/active</term><description>happy (filter), empty, 401</description></item>
/// <item><term>GET  /api/v1/admin/operations/type/{type}</term><description>happy</description></item>
/// <item><term>GET  /api/v1/admin/feature-events/replay</term><description>cursor+limit, time-window, no-cursor, empty, 401</description></item>
/// <item><term>POST /api/v1/admin/tile-operations/jobs</term><description>invalidate, invalid-op, 401</description></item>
/// <item><term>GET  /api/v1/admin/tile-operations/jobs/{id}</term><description>happy, 404</description></item>
/// <item><term>GET  /api/v1/admin/tile-operations/jobs</term><description>happy, 401</description></item>
/// <item><term>POST /api/v1/admin/tile-operations/jobs/{id}/cancel</term><description>not-found, active</description></item>
/// <item><term>POST /api/v1/admin/tile-operations/jobs/{id}/retry</term><description>happy (failed)</description></item>
/// <item><term>GET  /api/v1/admin/alerts/zones</term><description>happy, 401</description></item>
/// <item><term>POST /api/v1/admin/alerts/zones</term><description>happy, bad-wkt, 401</description></item>
/// <item><term>GET  /api/v1/admin/alerts/zones/{id}</term><description>happy</description></item>
/// <item><term>PUT  /api/v1/admin/alerts/zones/{id}</term><description>happy, 404</description></item>
/// <item><term>DELETE /api/v1/admin/alerts/zones/{id}</term><description>happy, 404</description></item>
/// <item><term>GET  /api/v1/admin/alerts/rules</term><description>happy, 401</description></item>
/// <item><term>POST /api/v1/admin/alerts/rules</term><description>happy, unconfigured-channel, 401</description></item>
/// <item><term>POST /api/v1/admin/alerts/rules/test</term><description>draft-zone, invalid-threshold</description></item>
/// <item><term>GET  /api/v1/admin/alerts/rules/{id}</term><description>happy</description></item>
/// <item><term>PUT  /api/v1/admin/alerts/rules/{id}</term><description>happy, 404</description></item>
/// <item><term>PUT  /api/v1/admin/alerts/rules/{id}/enabled</term><description>happy</description></item>
/// <item><term>GET  /api/v1/admin/alerts/rules/{id}/health</term><description>active-incidents, recent-trigger, rate-limited delivery</description></item>
/// <item><term>DELETE /api/v1/admin/alerts/rules/{id}</term><description>happy, 404</description></item>
/// </list>
/// <para>Endpoint-to-Scenario Matrix (Geocoding REST):</para>
/// <list type="table">
/// <item><term>GET  /rest/services/{loc}/GeocodeServer</term><description>capabilities, disabled-404</description></item>
/// <item><term>GET  /rest/services/{loc}/GeocodeServer/findAddressCandidates</term><description>happy, pjson, bad-format, bad-locator-404</description></item>
/// <item><term>POST /rest/services/{loc}/GeocodeServer/findAddressCandidates</term><description>happy (form-post)</description></item>
/// <item><term>GET  /rest/services/{loc}/GeocodeServer/reverseGeocode</term><description>happy, missing-location-400</description></item>
/// <item><term>POST /rest/services/{loc}/GeocodeServer/reverseGeocode</term><description>happy (form-post)</description></item>
/// <item><term>GET  /rest/services/{loc}/GeocodeServer/suggest</term><description>happy, unsupported-400</description></item>
/// <item><term>GET  /rest/services/{loc}/GeocodeServer/geocodeAddresses</term><description>happy, unsupported-400</description></item>
/// </list>
/// <para>Endpoint-to-Scenario Matrix (Geometry Service):</para>
/// <list type="table">
/// <item><term>POST /rest/services/Utilities/Geometry/GeometryServer/intersect</term><description>happy, no-overlap, missing-sr, malformed</description></item>
/// <item><term>GET  /rest/services/Utilities/Geometry/GeometryServer/intersect</term><description>happy, missing-params</description></item>
/// <item><term>POST /rest/services/Utilities/Geometry/GeometryServer/union</term><description>happy, single-geom, missing-sr, malformed</description></item>
/// <item><term>GET  /rest/services/Utilities/Geometry/GeometryServer/union</term><description>happy, missing-params</description></item>
/// <item><term>POST /rest/services/Utilities/Geometry/GeometryServer/difference</term><description>happy, no-overlap, missing-sr, malformed</description></item>
/// <item><term>GET  /rest/services/Utilities/Geometry/GeometryServer/difference</term><description>happy, missing-params</description></item>
/// <item><term>POST /rest/services/Utilities/Geometry/GeometryServer/clip</term><description>happy, envelope-semantics</description></item>
/// <item><term>GET  /rest/services/Utilities/Geometry/GeometryServer/clip</term><description>missing-params</description></item>
/// <item><term>POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths</term><description>happy (projected, geographic), missing-sr</description></item>
/// <item><term>GET  /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths</term><description>missing-params</description></item>
/// <item><term>POST /rest/services/Utilities/Geometry/GeometryServer/lengths</term><description>happy (projected, geographic), missing-sr</description></item>
/// <item><term>GET  /rest/services/Utilities/Geometry/GeometryServer/lengths</term><description>missing-params</description></item>
/// </list>
/// </remarks>
[Protocol(TestProtocols.Comprehensive)]
public sealed class ContractCoverageMatrixTests
{
    /// <summary>
    /// The minimum number of Admin endpoints that must retain multiple scenario tests.
    /// </summary>
    private const int MinimumAdminEndpointsWithScenarioDepth = 30;

    /// <summary>
    /// The minimum number of Geocoding endpoints that must retain multiple scenario tests.
    /// </summary>
    private const int MinimumGeocodingEndpointsWithScenarioDepth = 7;

    /// <summary>
    /// The minimum number of Geometry Service endpoints that must retain multiple scenario tests.
    /// </summary>
    private const int MinimumGeometryServiceEndpointsWithScenarioDepth = 12;

    [Fact]
    [Trait("Category", "Architecture")]
    public void Admin_EndpointCoverage_MeetsMinimumDepth()
    {
        var adminTestTypes = typeof(ContractCoverageMatrixTests).Assembly.GetTypes()
            .Where(type => type.Namespace is not null
                && (type.Namespace.Contains("Admin", StringComparison.Ordinal)
                    || type.Name.Contains("Admin", StringComparison.Ordinal)))
            .ToArray();

        var endpointsWithDepth = CountEndpointsWithMinimumScenarioCoverage(adminTestTypes, 2);

        endpointsWithDepth.Should().BeGreaterOrEqualTo(MinimumAdminEndpointsWithScenarioDepth,
            $"Admin area should retain multiple-scenario coverage on at least {MinimumAdminEndpointsWithScenarioDepth} endpoints but found {endpointsWithDepth}");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Geocoding_EndpointCoverage_MeetsMinimumDepth()
    {
        var geocodingTestTypes = typeof(ContractCoverageMatrixTests).Assembly.GetTypes()
            .Where(type => type.Namespace is not null
                && type.Namespace.Contains("Geocoding", StringComparison.Ordinal))
            .ToArray();

        var endpointsWithDepth = CountEndpointsWithMinimumScenarioCoverage(geocodingTestTypes, 2);

        endpointsWithDepth.Should().BeGreaterOrEqualTo(MinimumGeocodingEndpointsWithScenarioDepth,
            $"Geocoding area should retain multiple-scenario coverage on at least {MinimumGeocodingEndpointsWithScenarioDepth} endpoints but found {endpointsWithDepth}");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void GeometryService_EndpointCoverage_MeetsMinimumDepth()
    {
        // The GeometryService scenario tests were extracted into
        // Honua.Protocols.GeoServices.Tests, so scan that assembly alongside
        // Server.Tests (referenced by this project for exactly this reflection).
        var geometryServiceTestAssembly =
            System.Reflection.Assembly.Load("Honua.Protocols.GeoServices.Tests");
        var geometryTestTypes = new[] { typeof(ContractCoverageMatrixTests).Assembly, geometryServiceTestAssembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace is not null
                && type.Namespace.Contains("GeometryService", StringComparison.Ordinal))
            .ToArray();

        var endpointsWithDepth = CountEndpointsWithMinimumScenarioCoverage(geometryTestTypes, 2);

        endpointsWithDepth.Should().BeGreaterOrEqualTo(MinimumGeometryServiceEndpointsWithScenarioDepth,
            $"GeometryService area should retain multiple-scenario coverage on at least {MinimumGeometryServiceEndpointsWithScenarioDepth} endpoints but found {endpointsWithDepth}");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void AdminDeploy_Endpoints_HaveMultipleScenarioCoverage()
    {
        var deployTestType = typeof(ContractCoverageMatrixTests).Assembly.GetTypes()
            .Single(type => type.Name == "DeployControlEndpointsTests");

        var endpointCounts = CountEndpointScenarios([deployTestType]);
        var expectedDeployEndpoints = new[]
        {
            "GET /api/v1/admin/deploy/preflight",
            "POST /api/v1/admin/deploy/plan",
            "POST /api/v1/admin/deploy/operations",
            "GET /api/v1/admin/deploy/operations/{operationId}",
            "POST /api/v1/admin/deploy/operations/{operationId}/submit",
            "POST /api/v1/admin/deploy/operations/{operationId}/rollback"
        };

        foreach (var endpoint in expectedDeployEndpoints)
        {
            endpointCounts.Should().ContainKey(endpoint, $"deploy coverage should include {endpoint}");
            endpointCounts[endpoint].Should().BeGreaterOrEqualTo(2,
                $"{endpoint} should have both happy-path and negative-path coverage");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Admin_AllEndpointGroups_HaveAuthorizationTests()
    {
        var authTestType = typeof(ContractCoverageMatrixTests).Assembly.GetTypes()
            .SingleOrDefault(type => type.Name == "AdminAuthorizationTests");

        authTestType.Should().NotBeNull("AdminAuthorizationTests class must exist");

        var authEndpoints = authTestType!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(method => method.GetCustomAttributes<EndpointAttribute>())
            .Select(attr => attr.Endpoint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // At least one auth test per endpoint group
        authEndpoints.Length.Should().BeGreaterOrEqualTo(20,
            "auth tests should cover at least 20 distinct admin endpoints");
    }

    private static int CountEndpointsWithMinimumScenarioCoverage(IEnumerable<Type> testTypes, int minimumScenarioCount)
        => CountEndpointScenarios(testTypes).Count(pair => pair.Value >= minimumScenarioCount);

    private static Dictionary<string, int> CountEndpointScenarios(IEnumerable<Type> testTypes)
        => testTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(method => method.GetCustomAttributes<EndpointAttribute>()
                .Select(attr => attr.Endpoint))
            .GroupBy(endpoint => endpoint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
}
