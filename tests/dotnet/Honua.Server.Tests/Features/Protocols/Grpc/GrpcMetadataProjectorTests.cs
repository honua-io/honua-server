// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
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
