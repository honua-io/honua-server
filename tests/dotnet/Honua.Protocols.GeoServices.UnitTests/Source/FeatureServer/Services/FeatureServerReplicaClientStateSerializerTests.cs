// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class FeatureServerReplicaClientStateSerializerTests
{
    [Fact]
    public void Serialize_AttributeOnlyUpdate_OmitsGeometry()
    {
        var feature = new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?> { ["objectid"] = 7L },
            IncludeGeometry = false,
        };

        var json = FeatureServerReplicaClientStateSerializer.Instance.Serialize(
            new ReplicaUploadEdit(FeatureEditOperationKind.Update, 7, feature));

        using var envelope = JsonDocument.Parse(json!);
        envelope.RootElement.TryGetProperty("geometry", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_ExplicitNullGeometry_PreservesClearIntent()
    {
        var feature = new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?> { ["objectid"] = 7L },
            Geometry = null,
            IncludeGeometry = true,
        };

        var json = FeatureServerReplicaClientStateSerializer.Instance.Serialize(
            new ReplicaUploadEdit(FeatureEditOperationKind.Update, 7, feature));

        using var envelope = JsonDocument.Parse(json!);
        envelope.RootElement.TryGetProperty("geometry", out var geometry).Should().BeTrue();
        geometry.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
