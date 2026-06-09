// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ensures every registered endpoint has at least one integration test.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ApiSurfaceCoverageTests
{
    [ArchitectureTest]
    public void AllRegisteredEndpoints_HaveIntegrationTests()
    {
        var testedEndpoints = CollectIntegrationTestEndpoints();

        var missing = EndpointRegistry.All
            .Where(endpoint => !IsCovered(endpoint, testedEndpoints))
            .Select(endpoint => FormatKey(endpoint.Method, endpoint.Path))
            .OrderBy(key => key)
            .ToArray();

        missing.Should().BeEmpty("every registered endpoint must have at least one integration test");
    }

    private static HashSet<string> CollectIntegrationTestEndpoints()
    {
        // Server.Tests plus every extracted Honua.Protocols.<X>.Tests assembly:
        // OData / Mcp / etc. integration tests now live in their own projects.
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in ArchitectureTestHelpers.IntegrationTestMethods())
        {
            foreach (var endpointAttribute in method.GetCustomAttributes(typeof(EndpointAttribute), inherit: true).Cast<EndpointAttribute>())
            {
                endpoints.Add(NormalizeEndpoint(endpointAttribute.Endpoint));
            }
        }

        return endpoints;
    }

    private static bool IsCovered(EndpointDefinition endpoint, HashSet<string> testedEndpoints)
    {
        var expected = FormatKey(endpoint.Method, endpoint.Path);
        var wildcard = FormatKey("*", endpoint.Path);

        return testedEndpoints.Contains(expected) || testedEndpoints.Contains(wildcard);
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            return trimmed;
        }

        return FormatKey(parts[0], parts[1]);
    }

    private static string FormatKey(string method, string path)
    {
        var normalizedMethod = method.Trim().ToUpperInvariant();
        var normalizedPath = path.Trim();

        if (!normalizedPath.StartsWith('/'))
        {
            normalizedPath = "/" + normalizedPath;
        }

        return $"{normalizedMethod} {normalizedPath}";
    }
}
