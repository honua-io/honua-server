// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.StudioAiProxy;

[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyJsonContextReflectionSafetyTests
{
    // Mirrors the published image: only source-generated metadata, no reflection fallback.
    private static readonly JsonSerializerOptions AotOptions = new()
    {
        TypeInfoResolver = StudioAiProxyJsonContext.Default,
    };

    [UnitTest]
    public void SpecificToolChoicePayload_SerializesWithoutReflectionFallback()
    {
        var dto = new OpenAiProxyToolChoiceSpecific
        {
            Function = new OpenAiProxyToolChoiceFunctionRef { Name = "list_incidents" }
        };

        var act = () => JsonSerializer.SerializeToElement(dto, StudioAiProxyJsonContext.Default.OpenAiProxyToolChoiceSpecific);

        act.Should().NotThrow();
    }

    [UnitTest]
    public void SpecificToolChoicePayload_ResolvesThroughAotOnlyOptions()
    {
        var dto = new OpenAiProxyToolChoiceSpecific
        {
            Function = new OpenAiProxyToolChoiceFunctionRef { Name = "list_incidents" }
        };

        var json = JsonSerializer.Serialize(dto, AotOptions.GetTypeInfo<OpenAiProxyToolChoiceSpecific>());

        json.Should().Be("""{"type":"function","function":{"name":"list_incidents"}}""");
    }

    [UnitTest]
    public void ToolChoiceStringLiterals_SerializeWithoutReflectionFallback()
    {
        var required = () => JsonSerializer.SerializeToElement("required", StudioAiProxyJsonContext.Default.String);
        var auto = () => JsonSerializer.SerializeToElement("auto", StudioAiProxyJsonContext.Default.String);

        required.Should().NotThrow();
        auto.Should().NotThrow();
    }
}
