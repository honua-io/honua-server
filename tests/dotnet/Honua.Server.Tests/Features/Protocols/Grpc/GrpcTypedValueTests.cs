// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcTypedValueTests
{
    private const string GlobalId = "6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f";

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, Proto.FieldType.Decimal)]
    [InlineData(MetadataV2FieldType.TimestampOffset, Proto.FieldType.TimestampOffset)]
    public void ToProtoField_ExactCanonicalType_AdvertisesMatchingWireType(MetadataV2FieldType canonical, Proto.FieldType wire)
        => GrpcConversionHelpers.ToProtoField(new MetadataV2Field { Name = "value", TypeValue = canonical })
            .FieldType.Should().Be(wire);

    [UnitTest]
    public void ToFeatureEditBatch_PreciseCases_ParseLosslessly()
    {
        var add = new Proto.Feature();
        add.Attributes["day"] = new Proto.AttributeValue { DateValue = "2026-11-14" };
        add.Attributes["opens"] = new Proto.AttributeValue { TimeValue = "08:30:00.1234567" };
        add.Attributes["signed"] = new Proto.AttributeValue { TimestampOffsetValue = "2026-11-14T08:30:00.5-08:00" };
        add.Attributes["fee"] = new Proto.AttributeValue { DecimalValue = "12345678901234567890.10" };
        var request = new Proto.ApplyEditsRequest { ServiceId = "s", LayerId = 0 };
        request.Adds.Add(add);

        var attributes = GrpcConversionHelpers.ToFeatureEditBatch(request).Creates[0].Attributes;

        // Dates and times are stored as canonical text, as the other edit protocols store them.
        attributes["day"].Should().Be("2026-11-14");
        attributes["opens"].Should().Be("08:30:00.1234567");
        attributes["signed"].Should().Be(new GrpcExactValue(MetadataV2FieldType.TimestampOffset, "2026-11-14T08:30:00.5-08:00"));
        attributes["fee"].Should().Be(new GrpcExactValue(MetadataV2FieldType.Decimal, "12345678901234567890.10"));
    }

    [UnitTheory]
    [InlineData("2026-02-30")]
    [InlineData("0000-01-01")]
    [InlineData("2026-1-01")]
    public void ParseDate_InvalidCalendarDate_Throws(string value)
        => FluentActions.Invoking(() => GrpcTypedValues.ParseDate(value)).Should().Throw<ArgumentException>();

    [UnitTheory]
    [InlineData("24:00:00")]
    [InlineData("8:30:00")]
    [InlineData("08:30:00.12345678")]
    public void ParseTime_InvalidLocalTime_Throws(string value)
        => FluentActions.Invoking(() => GrpcTypedValues.ParseTime(value)).Should().Throw<ArgumentException>();

    [UnitTheory]
    [InlineData("2026-11-14T01:02:03+14:01")]
    [InlineData("2026-11-14T01:02:03")]
    [InlineData("2026-11-14 01:02:03Z")]
    [InlineData("2026-11-14T01:02:03+05:60")]
    [InlineData("2026-11-14T01:02:03Z\n")]
    [InlineData("2026-11-14T01:02:03+\u0660\u0661:00")]
    public void ParseTimestampOffset_InvalidValue_Throws(string value)
        => FluentActions.Invoking(() => GrpcTypedValues.ParseTimestampOffset(value)).Should().Throw<ArgumentException>();

    [UnitTheory]
    [InlineData("2026-11-14T01:02:03Z", 0)]
    [InlineData("2026-11-14T01:02:03+14:00", 840)]
    [InlineData("2026-11-14T01:02:03.1234567-05:45", -345)]
    public void ParseTimestampOffset_KeepsOffset(string value, int offsetMinutes)
        => GrpcTypedValues.ParseTimestampOffset(value).Offset.Should().Be(TimeSpan.FromMinutes(offsetMinutes));

    [UnitTheory]
    [InlineData("1e6")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1.")]
    public void ParseDecimal_InvalidText_Throws(string value)
        => FluentActions.Invoking(() => GrpcTypedValues.ParseDecimal(value)).Should().Throw<ArgumentException>();

    [UnitTheory]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("99999999999999999999999999999999")]
    public void ParseDecimal_BeyondServerPrecision_IsUnstorable(string value)
        => FluentActions.Invoking(() => GrpcTypedValues.ParseDecimal(value)).Should().Throw<UnstorableValueException>();

    [UnitTheory]
    [InlineData("0.10")]
    [InlineData("-0")]
    [InlineData("-0.00")]
    [InlineData("-12.5")]
    public void ParseDecimal_ExactValue_KeepsScale(string value)
        => GrpcTypedValues.ParseDecimal(value).Should().Be(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

    [UnitTest]
    public void ToProtoFeature_DateTimeAndGuidValues_UseInvariantStrings()
    {
        var feature = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("day", new DateOnly(2026, 11, 14))
            .Add("opens", new TimeOnly(8, 30))
            .Add("opens_precise", new TimeOnly(8, 30, 0).Add(TimeSpan.FromTicks(5)))
            .Add("span", TimeSpan.FromMinutes(90))
            .Add("GlobalID", Guid.Parse(GlobalId.ToUpperInvariant())));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        // Clients before geospatial-grpc 1.2 cannot read date_value/time_value, so reads stay strings.
        proto.Attributes["day"].StringValue.Should().Be("2026-11-14");
        proto.Attributes["opens"].StringValue.Should().Be("08:30:00");
        proto.Attributes["opens_precise"].StringValue.Should().Be("08:30:00.0000005");
        proto.Attributes["span"].StringValue.Should().Be("01:30:00");
        proto.Attributes["GlobalID"].StringValue.Should().Be(GlobalId);
        GrpcGlobalIds.Read(feature.Attributes, "globalid").Should().Be(GlobalId);
    }

    [UnitTest]
    public void ToProtoApplyEditsResponse_ReportsNormalisedGlobalId()
    {
        var result = FeatureEditResult.Success(
            1, 0, 0, createResults: [EditOperationResult.Success(9, "{" + GlobalId.ToUpperInvariant() + "}")]);

        GrpcConversionHelpers.ToProtoApplyEditsResponse(result).AddResults[0].GlobalId.Should().Be(GlobalId);
    }

    [UnitTheory]
    [InlineData("delete_global_ids without use_global_ids")]
    [InlineData("integer deletes with use_global_ids")]
    [InlineData("integer update ID with use_global_ids")]
    public void EnsureSelectorMode_MixedSelectors_RejectsBeforeWrites(string scenario)
    {
        var request = MixedSelectorRequest(scenario);

        FluentActions.Invoking(() => GrpcGlobalIds.EnsureSelectorMode(request)).Should().Throw<ArgumentException>();
    }

    private static Proto.ApplyEditsRequest MixedSelectorRequest(string scenario)
    {
        var request = new Proto.ApplyEditsRequest { UseGlobalIds = scenario != "delete_global_ids without use_global_ids" };
        switch (scenario)
        {
            case "delete_global_ids without use_global_ids":
                request.DeleteGlobalIds.Add(GlobalId);
                break;
            case "integer deletes with use_global_ids":
                request.Deletes.Add(1);
                break;
            default:
                request.Updates.Add(new Proto.Feature { Id = 1, GlobalId = GlobalId });
                break;
        }

        return request;
    }

    [UnitTest]
    public void ParseDistinctSelectors_NormalisesAndRejectsBracesAndDuplicates()
    {
        GrpcGlobalIds.ParseDistinctSelectors([GlobalId.ToUpperInvariant()], "global_ids").Should().Equal(GlobalId);
        FluentActions.Invoking(() => GrpcGlobalIds.ParseSelector("{" + GlobalId + "}", "global_ids"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GrpcGlobalIds.ParseDistinctSelectors([GlobalId, GlobalId.ToUpperInvariant()], "global_ids"))
            .Should().Throw<ArgumentException>().WithMessage("*repeats*");
    }

    [UnitTest]
    public void EnsureSelectorMode_RepeatedGlobalIds_RejectsBeforeWrites()
    {
        var overlapping = new Proto.ApplyEditsRequest { UseGlobalIds = true };
        overlapping.Updates.Add(new Proto.Feature { GlobalId = GlobalId });
        overlapping.DeleteGlobalIds.Add(GlobalId);
        var duplicateAdds = new Proto.ApplyEditsRequest();
        duplicateAdds.Adds.Add(new Proto.Feature { GlobalId = GlobalId });
        duplicateAdds.Adds.Add(new Proto.Feature { GlobalId = GlobalId.ToUpperInvariant() });

        FluentActions.Invoking(() => GrpcGlobalIds.EnsureSelectorMode(overlapping)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GrpcGlobalIds.EnsureSelectorMode(duplicateAdds)).Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void ToFeatureEditBatch_GlobalIdDeletes_CountAsDeletesForAuthorizationAndLimits()
    {
        var request = new Proto.ApplyEditsRequest { UseGlobalIds = true };
        request.DeleteGlobalIds.Add(GlobalId);
        request.DeleteGlobalIds.Add("7f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f");

        GrpcConversionHelpers.ToFeatureEditBatch(request).Deletes.Should().HaveCount(2);
    }

    [UnitTest]
    public void Predicate_UuidColumnMatchesLowercase_TextColumnMatchesStoredSpellings()
    {
        var uuid = new MetadataV2Resource { SchemaFields = [new MetadataV2Field { Name = "GlobalID", TypeValue = MetadataV2FieldType.Uuid }] };
        var text = new MetadataV2Resource { SchemaFields = [new MetadataV2Field { Name = "GlobalID", TypeValue = MetadataV2FieldType.String }] };

        GrpcGlobalIds.Predicate(uuid, "GlobalID", [GlobalId]).Should().Be($"GlobalID IN ('{GlobalId}')");
        GrpcGlobalIds.Predicate(text, "GlobalID", [GlobalId]).Should().Contain($"'{{{GlobalId.ToUpperInvariant()}}}'");
        FluentActions.Invoking(() => GrpcGlobalIds.Predicate(uuid, "Global ID", [GlobalId])).Should().Throw<ArgumentException>();
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Double, "0.12345678901234567890")]
    [InlineData(MetadataV2FieldType.Double, "0.10")]
    [InlineData(MetadataV2FieldType.Float, "0.1")]
    [InlineData(MetadataV2FieldType.String, "1.50")]
    [InlineData(MetadataV2FieldType.Integer, "1")]
    [InlineData(MetadataV2FieldType.BigInteger, "1.0")]
    [InlineData(MetadataV2FieldType.Integer, "1.5")]
    [InlineData(MetadataV2FieldType.BigInteger, "9223372036854775808")]
    public void EnsureStorable_DecimalWithoutExactStorage_FailsPrecondition(MetadataV2FieldType type, string value)
        => FluentActions.Invoking(() => GrpcTypedValues.EnsureStorable(NumericResource(type), DecimalBatch(value)))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

    private static MetadataV2Resource NumericResource(MetadataV2FieldType type)
        => new() { SchemaFields = [new MetadataV2Field { Name = "n", TypeValue = type }] };

    private static FeatureEditBatch DecimalBatch(string value)
        => Batch(ImmutableDictionary<string, object?>.Empty.Add("n", GrpcTypedValues.ExactDecimal(value)));

    [UnitTest]
    public void EnsureStorable_OffsetTimestamp_FailsPrecondition()
    {
        var batch = Batch(ImmutableDictionary<string, object?>.Empty.Add("signed", DateTimeOffset.UnixEpoch));

        FluentActions.Invoking(() => GrpcTypedValues.EnsureStorable(new MetadataV2Resource(), batch))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [UnitTheory]
    [InlineData("-0.00", "2026-11-14T08:30:00.5000-08:00")]
    [InlineData("12345678901234567890.1000", "2026-11-14T08:30:00.0000001+05:45")]
    public void EnsureStorable_ExactSchema_PreservesWireTextAcrossCreatesUpdatesAndOperations(string amount, string offsetText)
    {
        var resource = new MetadataV2Resource { SchemaFields =
        [
            new() { Name = "AMOUNT", TypeValue = MetadataV2FieldType.Decimal },
            new() { Name = "signed", TypeValue = MetadataV2FieldType.TimestampOffset }
        ] };
        var feature = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("amount", GrpcTypedValues.ExactDecimal(amount))
            .Add("signed", GrpcTypedValues.ExactTimestampOffset(offsetText)));
        var batch = FeatureEditBatch.Create(creates: [feature], updates: [feature],
            operations: [FeatureEditOperation.Create(feature)],
            rollbackOnFailure: true);

        var normalized = GrpcTypedValues.EnsureStorable(resource, batch);

        normalized.RollbackOnFailure.Should().BeTrue();
        foreach (var stored in new[] { normalized.Creates[0], normalized.Updates[0], normalized.Operations[0].Feature!.Value })
        {
            stored.Attributes["amount"].Should().Be(amount);
            stored.Attributes["signed"].Should().Be(offsetText);
            var wire = GrpcConversionHelpers.ToProtoFeature(stored, fields: resource.SchemaFields);
            wire.Attributes["amount"].DecimalValue.Should().Be(amount);
            wire.Attributes["signed"].TimestampOffsetValue.Should().Be(offsetText);
        }
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "1.50")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-14T00:00:00Z")]
    public void EnsureStorable_ExactFieldRequiresTypedWireCase(MetadataV2FieldType type, string text)
        => FluentActions.Invoking(() => GrpcTypedValues.EnsureStorable(NumericResource(type),
                Batch(ImmutableDictionary<string, object?>.Empty.Add("n", text))))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "1e5")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-14")]
    public void ToProtoFeature_InvalidStoredExactText_FailsExplicitly(MetadataV2FieldType type, string text)
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("n", text));
        FluentActions.Invoking(() => GrpcConversionHelpers.ToProtoFeature(feature, fields: NumericResource(type).SchemaFields))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal)]
    [InlineData(MetadataV2FieldType.TimestampOffset)]
    public void EnsureStorable_ExactFieldAllowsNull(MetadataV2FieldType type)
    {
        var result = GrpcTypedValues.EnsureStorable(NumericResource(type),
            Batch(ImmutableDictionary<string, object?>.Empty.Add("n", null)));
        GrpcConversionHelpers.ToProtoFeature(result.Creates[0], fields: NumericResource(type).SchemaFields)
            .Attributes["n"].ValueCase.Should().Be(Proto.AttributeValue.ValueOneofCase.NullValue);
    }

    private static FeatureEditBatch Batch(ImmutableDictionary<string, object?> attributes)
        => FeatureEditBatch.Create(
            creates: [Feature.Create(0, null, attributes)],
            updates: [],
            deletes: []);
}
