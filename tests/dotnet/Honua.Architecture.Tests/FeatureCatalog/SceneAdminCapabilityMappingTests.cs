// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

[Trait("Category", "Architecture")]
public sealed class SceneAdminCapabilityMappingTests
{
    private const string AdminScenesPrefix = "/api/v1/admin/scenes";

    private static readonly string[] HostedTilesPrefixes =
    [
        AdminScenesPrefix + "/generate",
        AdminScenesPrefix + "/ingest/archive",
        AdminScenesPrefix + "/ingest/model",
        AdminScenesPrefix + "/jobs",
        AdminScenesPrefix + "/assets",
    ];

    [ArchitectureTest]
    public void AdminSceneRoutes_MapToTheCapabilityThatGatesThem()
    {
        var mapper = CapabilityRouteMapper.Load();
        var routes = EndpointRegistry.All
            .Where(endpoint => endpoint.Path.StartsWith(AdminScenesPrefix, StringComparison.Ordinal))
            .ToArray();

        var mismatches = routes
            .Select(endpoint => new
            {
                Key = EndpointKey.Format(endpoint.Method, endpoint.Path),
                Actual = mapper.Resolve("Admin API", endpoint.Path, endpoint.Method),
                Expected = ExpectedCapability(endpoint.Path),
            })
            .Where(row => !string.Equals(row.Actual, row.Expected, StringComparison.Ordinal))
            .Select(row => $"{row.Key}: {row.Actual} (expected {row.Expected})")
            .ToArray();

        routes.Should().NotBeEmpty();
        mismatches.Should().BeEmpty(
            "admin scene routes are mapped only when their scene capability is enabled, so "
            + "capability-route-mapping.v1.json must stamp that capability, not admin.control-plane.");
    }

    private static string ExpectedCapability(string path)
    {
        if (path.StartsWith(AdminScenesPrefix + "/ingest/citygml", StringComparison.Ordinal))
        {
            return "scene.bim-ingest";
        }

        if (path.StartsWith(AdminScenesPrefix + "/ingest/pointcloud", StringComparison.Ordinal))
        {
            return "scene.pointcloud-ingest";
        }

        return HostedTilesPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))
            ? "serve.3d-tiles-scene"
            : "scene.catalog";
    }
}
