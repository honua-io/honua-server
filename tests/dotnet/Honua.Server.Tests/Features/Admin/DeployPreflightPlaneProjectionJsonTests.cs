// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// The grounding oracle compares MCP platform-release status to admin preflight
/// by exact field set. A null runtime profile or artifact reference is an
/// observation (no pin), so the key must be present. Dropping it is
/// indistinguishable from a missing pin.
/// </summary>
public sealed class DeployPreflightPlaneProjectionJsonTests
{
    [UnitTest]
    public void Serialize_NullProfileAndArtifact_KeepsBothKeys()
    {
        var projection = new DeployPreflightPlaneProjection
        {
            Id = "geoprocessing-local",
            RuntimeProfile = null,
            EffectiveArtifactReference = null,
            ProjectedFromRelease = false,
            Skewed = false,
        };

        using var document = JsonDocument.Parse(Serialize(projection));
        var root = document.RootElement;

        Assert.Equal("geoprocessing-local", root.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("runtimeProfile").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("effectiveArtifactReference").ValueKind);
        Assert.False(root.GetProperty("projectedFromRelease").GetBoolean());
        Assert.False(root.GetProperty("skewed").GetBoolean());
    }

    [UnitTest]
    public void Serialize_ExplicitPin_KeepsTheValues()
    {
        var projection = new DeployPreflightPlaneProjection
        {
            Id = "cert-ops-skew",
            RuntimeProfile = "managed",
            EffectiveArtifactReference = "ghcr.io/honua-io/honua-server@sha256:abc",
            ProjectedFromRelease = false,
            Skewed = true,
        };

        using var document = JsonDocument.Parse(Serialize(projection));
        var root = document.RootElement;

        Assert.Equal("managed", root.GetProperty("runtimeProfile").GetString());
        Assert.Equal("ghcr.io/honua-io/honua-server@sha256:abc", root.GetProperty("effectiveArtifactReference").GetString());
        Assert.True(root.GetProperty("skewed").GetBoolean());
    }

    private static string Serialize(DeployPreflightPlaneProjection projection) =>
        JsonSerializer.Serialize(projection, DeployControlJsonContext.Default.DeployPreflightPlaneProjection);
}
