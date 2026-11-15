// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcMetadataProjectorTests
{
    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "12345678901234567890.1000", Proto.AttributeValue.ValueOneofCase.DecimalValue)]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-15T00:01:02.1234567+05:45", Proto.AttributeValue.ValueOneofCase.TimestampOffsetValue)]
    public void Field_ExactDefaultsAndDomains_PreserveDeclaredTypeAndText(MetadataV2FieldType type,
        string text, Proto.AttributeValue.ValueOneofCase expected)
    {
        var definition = new MetadataV2Field
        {
            Name = "value", Type = type, Editable = true, DefaultValue = Json(JsonSerializer.Serialize(text)),
            Domain = new() { Type = "codedValue", CodedValues = [new() { Code = Json(JsonSerializer.Serialize(text)), Name = "Exact" }] }
        };

        var field = GrpcMetadataProjector.Field(definition);
        var template = GrpcMetadataProjector.Template("Default", [definition]);

        field.DefaultValue.ValueCase.Should().Be(expected);
        field.DefaultValue.Should().Be(field.Domain.CodedValues.Values.Single().Code);
        field.DefaultValue.Should().Be(template.DefaultAttributes["value"]);
        (type == MetadataV2FieldType.Decimal ? field.DefaultValue.DecimalValue : field.DefaultValue.TimestampOffsetValue)
            .Should().Be(text);
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "12345678901234567890.1000", "12345678901234567890.1000", Proto.AttributeValue.ValueOneofCase.DecimalValue)]
    [InlineData(MetadataV2FieldType.Decimal, "\"0.1000\"", "0.1000", Proto.AttributeValue.ValueOneofCase.DecimalValue)]
    [InlineData(MetadataV2FieldType.TimestampOffset, "\"2026-11-15T00:01:02.1234567+05:45\"", "2026-11-15T00:01:02.1234567+05:45", Proto.AttributeValue.ValueOneofCase.TimestampOffsetValue)]
    public void Subtypes_ExactDiscriminator_PreservesTypeAndTextInDefaultCodeAndTemplate(
        MetadataV2FieldType type, string json, string text, Proto.AttributeValue.ValueOneofCase expected)
    {
        var definition = GrpcMetadataProjector.Subtypes(new()
        {
            SubtypeField = "KIND", DefaultSubtypeCode = Json(json),
            Subtypes = [new() { Code = Json(json), Name = "Primary" }]
        }, [new() { Name = "kind", Type = type }]);

        var subtype = definition.Subtypes.Single();
        subtype.Code.ValueCase.Should().Be(expected);
        definition.DefaultCode.Should().Be(subtype.Code);
        subtype.Templates.Single().DefaultAttributes["KIND"].Should().Be(subtype.Code);
        (type == MetadataV2FieldType.Decimal ? subtype.Code.DecimalValue : subtype.Code.TimestampOffsetValue)
            .Should().Be(text);
    }

    [UnitTest]
    public void Subtypes_ExactOverrides_PreserveTypeInRangeAndTemplate()
    {
        MetadataV2Field[] fields =
        [
            new() { Name = "kind", Type = MetadataV2FieldType.Integer },
            new() { Name = "amount", Type = MetadataV2FieldType.Decimal, Editable = true }
        ];
        var definition = GrpcMetadataProjector.Subtypes(new()
        {
            SubtypeField = "kind", Subtypes = [new()
            {
                Code = Json("1"), Name = "Primary", FieldOverrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>
                {
                    ["AMOUNT"] = new()
                    {
                        DefaultValue = Json("\"0.1000\""),
                        Domain = new() { Type = "range", Range = [Json("\"0.0000\""), Json("\"99999999999999999999.1000\"")] }
                    }
                }
            }]
        }, fields).Subtypes.Single();

        definition.FieldOverrides["amount"].DefaultValue.DecimalValue.Should().Be("0.1000");
        definition.Templates.Single().DefaultAttributes["amount"].DecimalValue.Should().Be("0.1000");
        definition.FieldOverrides["amount"].Domain.Range.Minimum.DecimalValue.Should().Be("0.0000");
        definition.FieldOverrides["amount"].Domain.Range.Maximum.DecimalValue.Should().Be("99999999999999999999.1000");
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal)]
    [InlineData(MetadataV2FieldType.TimestampOffset)]
    public void Field_ExactNullDefault_RemainsExplicitNull(MetadataV2FieldType type)
        => GrpcMetadataProjector.Field(new() { Name = "value", Type = type, DefaultValue = Json("null") })
            .DefaultValue.ValueCase.Should().Be(Proto.AttributeValue.ValueOneofCase.NullValue);

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "\"not-a-number\"")]
    [InlineData(MetadataV2FieldType.Decimal, "\"79228162514264337593543950336\"")]
    [InlineData(MetadataV2FieldType.Decimal, "true")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "123")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "\"2026-11-15T00:00:00\"")]
    public void Field_InvalidExactDefault_FailsWithoutDisclosingValue(MetadataV2FieldType type, string json)
    {
        var act = () => GrpcMetadataProjector.Field(new() { Name = "value", Type = type, DefaultValue = Json(json) });

        var exception = act.Should().Throw<RpcException>().Which;
        exception.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        exception.Status.Detail.Should().Be("Exact metadata value does not match its declared type.");
    }

    [UnitTest]
    public void Domain_NumericDecimalBounds_PreservesScaleWithoutDoubleConversion()
    {
        var domain = GrpcMetadataProjector.Domain(new()
        {
            Type = "range", Range = [Json("-0.1000"), Json("12345678901234567890.1000")]
        }, MetadataV2FieldType.Decimal);

        domain.Range.Minimum.DecimalValue.Should().Be("-0.1000");
        domain.Range.Maximum.DecimalValue.Should().Be("12345678901234567890.1000");
    }

    [UnitTest]
    public void Field_TypedDefaultsAndDomain_PreservesIntegerPrecision()
    {
        var field = GrpcMetadataProjector.Field(new MetadataV2Field
        {
            Name = "asset",
            Type = MetadataV2FieldType.BigInteger,
            Nullable = true,
            Editable = false,
            DefaultValue = Json("9007199254740993"),
            Domain = new()
            {
                Name = "Assets",
                Type = "codedValue",
                CodedValues = [new() { Code = Json("9007199254740993"), Name = "Main" }]
            }
        });

        field.Editable.Should().BeFalse();
        field.DefaultValue.Int64Value.Should().Be(9007199254740993L);
        field.Domain.CodedValues.Values.Single().Code.Int64Value.Should().Be(9007199254740993L);
        field.Domain.CodedValues.Values.Single().Name.Should().Be("Main");
    }

    [UnitTheory]
    [InlineData("{\"enabled\":true}")]
    [InlineData("[1,\"two\",null]")]
    public void Field_JsonDefault_PreservesStructuredValueInDescriptorAndTemplate(string json)
    {
        var definition = new MetadataV2Field
        {
            Name = "settings",
            Type = MetadataV2FieldType.Json,
            Editable = true,
            DefaultValue = Json(json)
        };

        var field = GrpcMetadataProjector.Field(definition);
        var template = GrpcMetadataProjector.Template("Default", [definition]);

        field.DefaultValue.StringValue.Should().Be(json);
        template.DefaultAttributes["settings"].StringValue.Should().Be(json);
    }

    [UnitTest]
    public void Domain_Range_PreservesBoundsAndPolicies()
    {
        var domain = GrpcMetadataProjector.Domain(new MetadataV2FieldDomain
        {
            Type = "range",
            Range = [Json("-1.5"), Json("10.25")],
            MergePolicy = "sum",
            SplitPolicy = "duplicate"
        });

        domain.Range.Minimum.DoubleValue.Should().Be(-1.5);
        domain.Range.Maximum.DoubleValue.Should().Be(10.25);
        domain.MergePolicy.Should().Be("sum");
        domain.SplitPolicy.Should().Be("duplicate");
    }

    [UnitTest]
    public void Subtypes_VisibleFields_PreservesNullAndInheritanceWithoutMaskedDefaults()
    {
        MetadataV2Field[] fields =
        [
            new() { Name = "kind", Type = MetadataV2FieldType.Integer },
            new() { Name = "status", Type = MetadataV2FieldType.String, DefaultValue = Json("\"new\"") },
            new() { Name = "generated", Type = MetadataV2FieldType.String, Editable = false, DefaultValue = Json("\"server\"") }
        ];
        var definition = GrpcMetadataProjector.Subtypes(new()
        {
            SubtypeField = "kind",
            DefaultSubtypeCode = Json("1"),
            Subtypes = [new()
            {
                Code = Json("1"), Name = "Primary", FieldOverrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>
                {
                    ["STATUS"] = new() { DefaultValueIsNull = true },
                    ["secret"] = new() { DefaultValue = Json("\"classified\"") }
                }
            }]
        }, fields);

        definition.DefaultCode.Int32Value.Should().Be(1);
        var subtype = definition.Subtypes.Single();
        subtype.FieldOverrides.Keys.Should().Equal("status");
        subtype.FieldOverrides["status"].DefaultValue.ValueCase.Should().Be(Proto.AttributeValue.ValueOneofCase.NullValue);
        subtype.FieldOverrides["status"].DomainOverrideCase.Should().Be(Proto.SubtypeFieldOverride.DomainOverrideOneofCase.None);
        var defaults = subtype.Templates.Single().DefaultAttributes;
        defaults.Keys.Should().BeEquivalentTo(["kind", "status"]);
        defaults["status"].ValueCase.Should().Be(Proto.AttributeValue.ValueOneofCase.NullValue);
        defaults["kind"].Int32Value.Should().Be(1);
    }

    [UnitTest]
    public void Field_AbsentAndNullDefaults_RemainDistinct()
    {
        GrpcMetadataProjector.Field(new() { Name = "absent" }).DefaultValue.Should().BeNull();
        GrpcMetadataProjector.Field(new() { Name = "null", DefaultValue = Json("null") })
            .DefaultValue.ValueCase.Should().Be(Proto.AttributeValue.ValueOneofCase.NullValue);
    }

    [UnitTest]
    public void Time_MaskedEndFieldAndUnknownZone_DoesNotInventMetadata()
    {
        var time = GrpcMetadataProjector.Time(new()
        {
            StartTimeField = "observed",
            EndTimeField = "secret",
            Extent = new() { Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch.AddDays(1) }
        }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "observed" });

        time.StartField.Should().Be("observed");
        time.EndField.Should().BeEmpty();
        time.TimeZone.Should().BeEmpty();
        time.HasRespectsDaylightSaving.Should().BeFalse();
        time.ExtentStartUtcMs.Should().Be(0);
        time.ExtentEndUtcMs.Should().Be(86400000);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
