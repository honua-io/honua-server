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

        attributes["day"].Should().Be(new DateOnly(2026, 11, 14));
        attributes["opens"].Should().Be(new TimeOnly(8, 30, 0).Add(TimeSpan.FromTicks(1234567)));
        attributes["signed"].Should().Be(new DateTimeOffset(2026, 11, 14, 8, 30, 0, 500, TimeSpan.FromHours(-8)));
        ((decimal)attributes["fee"]!).ToString(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("12345678901234567890.10");
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
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("99999999999999999999999999999999")]
    public void ParseDecimal_InexactOrInvalid_Throws(string value)
        => FluentActions.Invoking(() => GrpcTypedValues.ParseDecimal(value)).Should().Throw<ArgumentException>();

    [UnitTheory]
    [InlineData("0.10")]
    [InlineData("-0")]
    [InlineData("-0.00")]
    [InlineData("-12.5")]
    public void ParseDecimal_ExactValue_KeepsScale(string value)
        => GrpcTypedValues.ParseDecimal(value).Should().Be(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

    [UnitTest]
    public void ToProtoFeature_DateTimeAndGuidValues_UseInvariantWireForms()
    {
        var feature = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("day", new DateOnly(2026, 11, 14))
            .Add("opens", new TimeOnly(8, 30))
            .Add("opens_precise", new TimeOnly(8, 30, 0).Add(TimeSpan.FromTicks(5)))
            .Add("span", TimeSpan.FromMinutes(90))
            .Add("GlobalID", Guid.Parse(GlobalId.ToUpperInvariant())));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature, globalIdFieldName: "globalid");

        proto.Attributes["day"].DateValue.Should().Be("2026-11-14");
        proto.Attributes["opens"].TimeValue.Should().Be("08:30:00");
        proto.Attributes["opens_precise"].TimeValue.Should().Be("08:30:00.0000005");
        proto.Attributes["span"].TimeValue.Should().Be("01:30:00");
        proto.Attributes["GlobalID"].StringValue.Should().Be(GlobalId);
        proto.GlobalId.Should().Be(GlobalId);
    }

    [UnitTest]
    public void ToProtoApplyEditsResponse_ReportsNormalisedGlobalId()
    {
        var result = FeatureEditResult.Success(
            1, 0, 0, createResults: [EditOperationResult.Success(9, "{" + GlobalId.ToUpperInvariant() + "}")]);

        GrpcConversionHelpers.ToProtoApplyEditsResponse(result).AddResults[0].GlobalId.Should().Be(GlobalId);
    }

    [UnitTheory]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void EnsureSelectorMode_MixedSelectors_RejectsBeforeWrites(
        bool useGlobalIds, bool deleteGlobalIds, bool integerDeletes, bool integerUpdate)
    {
        var request = new Proto.ApplyEditsRequest { UseGlobalIds = useGlobalIds };
        if (deleteGlobalIds) request.DeleteGlobalIds.Add(GlobalId);
        if (integerDeletes) request.Deletes.Add(1);
        if (integerUpdate) request.Updates.Add(new Proto.Feature { Id = 1, GlobalId = GlobalId });

        FluentActions.Invoking(() => GrpcGlobalIds.EnsureSelectorMode(request)).Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void ParseSelectors_NormalisesAndRejectsBraces()
    {
        GrpcGlobalIds.ParseSelectors([GlobalId.ToUpperInvariant(), GlobalId], "global_ids").Should().Equal(GlobalId);
        FluentActions.Invoking(() => GrpcGlobalIds.ParseSelector("{" + GlobalId + "}", "global_ids"))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    public void CombineWhere_NarrowsTheCallersWhere()
    {
        GrpcGlobalIds.CombineWhere("1=1", "GlobalID", [GlobalId]).Should().Be($"GlobalID IN ('{GlobalId}')");
        GrpcGlobalIds.CombineWhere("status = 'open'", "GlobalID", [GlobalId])
            .Should().Be($"(status = 'open') AND GlobalID IN ('{GlobalId}')");
    }

    [UnitTest]
    public void RequireField_LayerWithoutGlobalIdField_FailsPrecondition()
        => FluentActions.Invoking(() => GrpcGlobalIds.RequireField(new MetadataV2Resource()))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Double, "0.1", false)]
    [InlineData(MetadataV2FieldType.Double, "0.12345678901234567890", true)]
    [InlineData(MetadataV2FieldType.Integer, "1.5", true)]
    [InlineData(MetadataV2FieldType.BigInteger, "9223372036854775808", true)]
    [InlineData(MetadataV2FieldType.String, "1.5", false)]
    public void EnsureStorable_DecimalThatWouldRound_FailsPrecondition(MetadataV2FieldType type, string value, bool refused)
    {
        var resource = new MetadataV2Resource { SchemaFields = [new MetadataV2Field { Name = "n", TypeValue = type }] };
        var batch = Batch(ImmutableDictionary<string, object?>.Empty.Add("n", GrpcTypedValues.ParseDecimal(value)));

        var act = () => GrpcTypedValues.EnsureStorable(resource, batch);

        if (refused)
        {
            act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        }
        else
        {
            act.Should().NotThrow();
        }
    }

    [UnitTest]
    public void EnsureStorable_OffsetTimestamp_FailsPrecondition()
    {
        var batch = Batch(ImmutableDictionary<string, object?>.Empty.Add("signed", DateTimeOffset.UnixEpoch));

        FluentActions.Invoking(() => GrpcTypedValues.EnsureStorable(new MetadataV2Resource(), batch))
            .Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    private static FeatureEditBatch Batch(ImmutableDictionary<string, object?> attributes)
        => FeatureEditBatch.Create(
            creates: [Feature.Create(0, null, attributes)],
            updates: [],
            deletes: []);
}
