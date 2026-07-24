// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Reproduces the reflection-disabled constraint the published server image runs under
/// (<c>JsonSerializerIsReflectionEnabledByDefault=false</c> in <c>Honua.Server.csproj</c>): every
/// CLR type the Studio AI proxy hands to <c>JsonSerializer.SerializeToElement</c> /
/// <c>Serialize</c> must have a source-generated <c>JsonTypeInfo</c> registered in
/// <see cref="StudioAiProxyJsonContext"/>. The rest of this test project runs with reflection
/// enabled (the xUnit test-host default), which would silently mask an accidental anonymous-type
/// <c>SerializeToElement</c> call or a missing registration; this test forces source-generated-only
/// metadata (mirrors <c>MapServerFindSerializationTests</c>) so the gap surfaces as a failing test
/// instead of a runtime throw on the AOT image (honua-server#3010 review).
/// </summary>
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

        // The pre-fix adapter built this shape with an anonymous type passed to the
        // parameterless SerializeToElement(value) overload, which resolves metadata through
        // JsonSerializerOptions.Default and throws once reflection fallback is unavailable.
        // Calling through the explicit source-generated JsonTypeInfo, as the fixed adapter does,
        // must never depend on reflection at all.
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

        var json = JsonSerializer.Serialize(dto, AotOptions.GetTypeInfo(typeof(OpenAiProxyToolChoiceSpecific)));

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
