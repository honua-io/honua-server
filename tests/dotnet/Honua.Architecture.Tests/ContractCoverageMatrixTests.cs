// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Architecture.Tests;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;
using TestProtocols = Honua.TestKit.Constants.ProtocolNames;

namespace Honua.Server.Tests.Comprehensive;

[Protocol(TestProtocols.Comprehensive)]
public sealed class ContractCoverageMatrixTests
{
    private const int MinimumAdminEndpointsWithScenarioDepth = 30;

    private const int MinimumGeocodingEndpointsWithScenarioDepth = 7;

    private const int MinimumGeometryServiceEndpointsWithScenarioDepth = 12;

    [Fact]
    [Trait("Category", "Architecture")]
    public void Admin_EndpointCoverage_MeetsMinimumDepth()
    {
        var adminTestTypes = ArchitectureTestHelpers.IntegrationTestAssemblies().SelectMany(assembly => assembly.GetTypes())
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
        var geocodingTestTypes = ArchitectureTestHelpers.IntegrationTestAssemblies().SelectMany(assembly => assembly.GetTypes())
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
        var geometryTestTypes = ArchitectureTestHelpers.IntegrationTestAssemblies()
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
        var deployTestType = ArchitectureTestHelpers.IntegrationTestAssemblies().SelectMany(assembly => assembly.GetTypes())
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
        var authTestType = ArchitectureTestHelpers.IntegrationTestAssemblies().SelectMany(assembly => assembly.GetTypes())
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
