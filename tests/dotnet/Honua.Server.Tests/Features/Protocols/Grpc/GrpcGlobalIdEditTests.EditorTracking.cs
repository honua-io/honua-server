// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

// Editor-tracking parity with GeoServices applyEdits: client values never reach the writer,
// and the caller and server clock are stamped on insert (all four) and update (editor only).
public sealed partial class GrpcGlobalIdEditTests
{
    private const string Caller = "test-user";

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_Insert_ReplacesClientTrackingValuesWithCallerAndServerTime()
    {
        UseLayer(TrackedResource(MetadataV2FieldType.DateTime), capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(ForgedTrackingFeature(id: 0));
        var before = DateTime.UtcNow;

        await _sut.ApplyEdits(request, CallContext("insert"));

        var written = WrittenBatch().Creates.Single().Attributes;
        written["name"].Should().Be("kept");
        written["created_user"].Should().Be(Caller);
        written["last_edited_user"].Should().Be(Caller);
        AssertStampedSince(written["created_date"], before);
        AssertStampedSince(written["last_edited_date"], before);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_Update_DropsClientTrackingValuesAndStampsOnlyEditorFields()
    {
        UseLayer(TrackedResource(MetadataV2FieldType.DateTime), capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(0, 1, 0, updateResults: [EditOperationResult.Success(ResolvedObjectId)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Updates.Add(ForgedTrackingFeature(ResolvedObjectId));
        var before = DateTime.UtcNow;

        await _sut.ApplyEdits(request, CallContext("update"));

        var update = WrittenBatch().Updates.Single();
        update.PreserveOmittedMaskedAttributes.Should().BeTrue("the stored creator and created-at values are kept");
        update.Attributes.Should().NotContainKeys("created_user", "created_date");
        update.Attributes["name"].Should().Be("kept");
        update.Attributes["last_edited_user"].Should().Be(Caller);
        AssertStampedSince(update.Attributes["last_edited_date"], before);
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Date)]
    [InlineData(MetadataV2FieldType.String)]
    [InlineData(MetadataV2FieldType.BigInteger)]
    [InlineData(MetadataV2FieldType.Integer)]
    [InlineData(MetadataV2FieldType.TimestampOffset)]
    [InlineData(MetadataV2FieldType.Boolean)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_Insert_StampsTimeInTheDeclaredFieldType(MetadataV2FieldType type)
    {
        UseLayer(TrackedResource(type), capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature { Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "kept" } } });
        var before = DateTimeOffset.UtcNow;

        await _sut.ApplyEdits(request, CallContext("insert"));

        var written = WrittenBatch().Creates.Single().Attributes;
        written["created_user"].Should().Be(Caller);
        written.TryGetValue("created_date", out var stamped);
        switch (type)
        {
            case MetadataV2FieldType.Date:
                stamped.Should().BeOfType<string>().Which.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
                DateOnly.ParseExact((string)stamped!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    .Should().BeOnOrAfter(DateOnly.FromDateTime(before.UtcDateTime));
                break;
            case MetadataV2FieldType.String or MetadataV2FieldType.TimestampOffset:
                DateTimeOffset.Parse(stamped.Should().BeOfType<string>().Which, CultureInfo.InvariantCulture)
                    .Should().BeOnOrAfter(before);
                break;
            case MetadataV2FieldType.Integer or MetadataV2FieldType.Boolean:
                stamped.Should().BeNull("only time-capable types are stamped; a 32-bit field cannot hold epoch milliseconds");
                break;
            default:
                stamped.Should().BeOfType<long>().Which.Should().BeGreaterThanOrEqualTo(before.ToUnixTimeMilliseconds());
                break;
        }
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_ForgedValueWithWrongWireType_IsIgnoredNotRejected()
    {
        UseLayer(TrackedResource(MetadataV2FieldType.TimestampOffset), capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature { Attributes = { ["created_date"] = new Proto.AttributeValue { StringValue = "1999-01-01" } } });

        await _sut.ApplyEdits(request, CallContext("insert"));

        WrittenBatch().Creates.Single().Attributes["created_date"].Should().BeOfType<string>()
            .Which.Should().NotBe("1999-01-01");
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_LayerWithoutTrackingFields_WritesClientAttributesUnchanged()
    {
        ReturnWriteResult(FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature());

        await _sut.ApplyEdits(request, CallContext("insert"));

        WrittenBatch().Creates.Single().Attributes.Keys.Should().BeEquivalentTo(["GlobalID"]);
    }

    private static Proto.Feature ForgedTrackingFeature(long id)
    {
        var feature = new Proto.Feature { Id = id };
        feature.Attributes["name"] = new Proto.AttributeValue { StringValue = "kept" };
        feature.Attributes["created_user"] = new Proto.AttributeValue { StringValue = "admin" };
        feature.Attributes["last_edited_user"] = new Proto.AttributeValue { StringValue = "admin" };
        feature.Attributes["created_date"] = new Proto.AttributeValue { DatetimeValue = 0 };
        feature.Attributes["last_edited_date"] = new Proto.AttributeValue { DatetimeValue = 0 };
        return feature;
    }

    private static MetadataV2Object TrackedResource(MetadataV2FieldType timeType)
    {
        var resource = GlobalIdResource();
        return resource with
        {
            Editing = resource.Editing! with
            {
                CreatorField = "created_user",
                CreatedAtField = "created_date",
                EditorField = "last_edited_user",
                UpdatedAtField = "last_edited_date",
            },
            SchemaFields =
            [
                .. resource.SchemaFields,
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "created_user", Type = MetadataV2FieldType.String, Nullable = true, Editable = false },
                new MetadataV2Field { Name = "created_date", Type = timeType, Nullable = true, Editable = false },
                new MetadataV2Field { Name = "last_edited_user", Type = MetadataV2FieldType.String, Nullable = true, Editable = false },
                new MetadataV2Field { Name = "last_edited_date", Type = timeType, Nullable = true, Editable = false },
            ]
        };
    }

    private void ReturnWriteResult(FeatureEditResult result)
        => _featureWriter.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(Task.FromResult(result));

    private FeatureEditBatch WrittenBatch()
        => (FeatureEditBatch)_featureWriter.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync))
            .GetArguments()[1]!;

    private static void AssertStampedSince(object? value, DateTime before)
        => value.Should().BeOfType<DateTime>().Which.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
}
