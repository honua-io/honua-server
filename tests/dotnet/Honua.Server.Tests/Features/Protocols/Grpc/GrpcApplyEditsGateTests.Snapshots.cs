// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcApplyEditsGateTests
{
    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task MaskedUpdate_UsesTheUnmaskedSnapshotOnlyForValidation()
    {
        var full = ImmutableDictionary<string, object?>.Empty.Add("name", "stored").Add("color", "red").Add("size", "S");
        var stored = Feature.Create(StoredId, null, full.Remove("size")) with
        {
            ReadStateToken = "provider-full-row-token",
            EditValidationAttributes = full
        };
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(Arg.Any<int>(), StoredId, Arg.Any<CancellationToken>()).Returns(stored);
        var harness = Harness(reader);
        var update = new Proto.Feature { Id = StoredId, Attributes = { ["color"] = new Proto.AttributeValue { StringValue = "red" } } };

        var response = await harness.Service.ApplyEdits(RollbackRequest(updates: [update]), Context(harness));

        response.UpdateResults.Should().ContainSingle().Which.Success.Should().BeTrue();
        _hooks.BeforeBatches.Should().ContainSingle().Which.Updates.Should().ContainSingle()
            .Which.Attributes.Should().Contain("size", "S");
        _written.Should().ContainSingle().Which.Updates.Should().ContainSingle()
            .Which.Attributes.Should().NotContainKey("size");
        _written[0].Preconditions.Should().ContainSingle().Which.ExpectedStateToken.Should().Be("provider-full-row-token");
        _written[0].Preconditions[0].MaskedFields.Should().ContainSingle().Which.Should().Be("size");
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task MaskedUpdate_CannotUseAnOmittedHiddenValueAsNull()
    {
        var full = ImmutableDictionary<string, object?>.Empty.Add("color", "red").Add("size", "S");
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(Arg.Any<int>(), StoredId, Arg.Any<CancellationToken>()).Returns(
            Feature.Create(StoredId, null, full.Remove("size")) with { EditValidationAttributes = full });
        var resource = ShirtResource();
        var group = resource.ContingentValueGroups[0];
        var nullSize = new MetadataV2ContingentValue
        {
            Id = 2,
            Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["color"] = new() { Type = "code", Code = JsonElement.Parse("\"blue\"") },
                ["size"] = new() { Type = "null" }
            }
        };
        resource = resource with { ContingentValueGroups = [group with { ContingentValues = [.. group.ContingentValues, nullSize] }] };
        var harness = Harness(reader, resource);
        var update = new Proto.Feature { Id = StoredId, Attributes = { ["color"] = new Proto.AttributeValue { StringValue = "blue" } } };

        var exception = await CallAsync(harness, RollbackRequest(updates: [update]));

        exception!.StatusCode.Should().Be(StatusCode.InvalidArgument);
        _written.Should().BeEmpty();
    }

}
