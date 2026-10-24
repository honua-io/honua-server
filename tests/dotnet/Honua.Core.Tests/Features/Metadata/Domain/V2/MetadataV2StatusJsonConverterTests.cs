// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2StatusJsonConverterTests
{
    [UnitTest]
    public void Read_MixedCaseNames_PreservesExplicitDraft()
    {
        var status = JsonSerializer.Deserialize<MetadataV2Status>("""{"lifecycle":"Draft","state":"READY"}""");

        status.Should().NotBeNull();
        status!.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Draft);
        status.State.Should().Be(MetadataV2OperationalState.Ready);
    }

    [UnitTest]
    public void Read_NumericCodes_StayOnTheNumberPath()
    {
        var status = JsonSerializer.Deserialize<MetadataV2Status>("""{"lifecycle":1,"state":3}""");

        status!.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
        status.State.Should().Be(MetadataV2OperationalState.Degraded);
        var act = () => JsonSerializer.Deserialize<MetadataV2Status>("""{"lifecycle":"1"}""");
        act.Should().Throw<JsonException>();
    }

    [UnitTest]
    public void Read_OmittedLifecycle_StaysActive()
    {
        var status = JsonSerializer.Deserialize<MetadataV2Status>("{}");

        status!.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
    }

    [UnitTest]
    public void Write_WhenWritingNull_OmitsUnsetMembers()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new MetadataV2StatusJsonConverter() }
        };
        var status = new MetadataV2Status
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
            Conditions =
            [
                new MetadataV2Condition { Type = "Ready", Status = "True" }
            ]
        };

        var json = JsonSerializer.Serialize(status, options);

        json.Should().NotContain("observedAt");
        json.Should().NotContain("lastTransitionAt");
        json.Should().NotContain("reason");
        json.Should().NotContain("message");
        json.Should().Contain("\"lifecycle\":\"active\"");
    }

    [UnitTest]
    public void Write_DefaultOptions_KeepsNullMembers()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new MetadataV2StatusJsonConverter() }
        };
        var status = new MetadataV2Status
        {
            Conditions = [new MetadataV2Condition { Type = "Ready", Status = "True" }]
        };

        var json = JsonSerializer.Serialize(status, options);

        json.Should().Contain("\"observedAt\":null");
        json.Should().Contain("\"lastTransitionAt\":null");
        json.Should().Contain("\"reason\":null");
    }
}
