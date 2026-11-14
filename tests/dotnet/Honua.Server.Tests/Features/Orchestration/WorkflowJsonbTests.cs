// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Orchestration;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class WorkflowJsonbTests
{
    [UnitTheory]
    [InlineData("a\u0000b", "a�b")]
    [InlineData("\\\u0000", "\\�")]
    [InlineData("\\u0000", "\\u0000")]
    [InlineData("\\\\u0000", "\\\\u0000")]
    [InlineData("plain", "plain")]
    public void Serialize_ReplacesOnlyEncodedNulCharacters(string value, string expected)
    {
        var json = WorkflowJsonb.Serialize(new[] { value }, OrchestrationJsonContext.Default.StringArray);

        JsonSerializer.Deserialize(json, OrchestrationJsonContext.Default.StringArray)
            .Should().ContainSingle().Which.Should().Be(expected);
    }
}
