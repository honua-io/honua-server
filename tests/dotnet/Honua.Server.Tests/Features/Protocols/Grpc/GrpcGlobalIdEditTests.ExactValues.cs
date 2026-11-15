// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcGlobalIdEditTests
{
    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "12345678901234567890.1000")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-15T00:01:02.1234567-07:00")]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    public async Task ExactValue_ApplyThenQuery_PreservesTextAndWireType(MetadataV2FieldType type, string text)
    {
        var resource = GlobalIdResource();
        UseLayer(resource with
        {
            SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = "exact", Type = type, Nullable = true }]
        }, capabilities: null);
        _featureWriter.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(Task.FromResult(
            FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)])));
        var wireValue = type == MetadataV2FieldType.Decimal
            ? new Proto.AttributeValue { DecimalValue = text }
            : new Proto.AttributeValue { TimestampOffsetValue = text };
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        var add = new Proto.Feature();
        add.Attributes["exact"] = wireValue;
        request.Adds.Add(add);

        var edited = await _sut.ApplyEdits(request, CallContext("insert"));

        edited.AddResults.Should().ContainSingle().Which.Success.Should().BeTrue();
        var batch = (FeatureEditBatch)_featureWriter.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(_featureWriter.ApplyEditsAsync))
            .GetArguments()[1]!;
        batch.Creates.Single().Attributes["exact"].Should().BeOfType<string>().Which.Should().Be(text);
        _featureReader.QueryAsync(default, default!, default).ReturnsForAnyArgs(
            QueryResult<Feature>.Create(1, [Feature.Create(7, null, batch.Creates.Single().Attributes)]));

        var queried = await _sut.QueryFeatures(new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnGeometry = false
        }, CallContext("query"));

        queried.Features.Should().ContainSingle().Which.Attributes["exact"].Should().Be(wireValue);
        queried.Fields.Should().ContainSingle(field => field.Name == "exact")
            .Which.FieldType.Should().Be(type == MetadataV2FieldType.Decimal
                ? Proto.FieldType.Decimal : Proto.FieldType.TimestampOffset);
    }
}
