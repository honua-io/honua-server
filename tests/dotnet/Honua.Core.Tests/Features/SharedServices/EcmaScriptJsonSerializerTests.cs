// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.Shared.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedServices;

public sealed class EcmaScriptJsonSerializerTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{ "a" : [ 1.50, 1e2, -0, true, false, null ], "b" : {} }""", """{"a":[1.5,100,0,true,false,null],"b":{}}""")]
    [InlineData("""{"text":"\u00e9 \ud83d\ude00 <&'/"}""", "{\"text\":\"\u00e9 \ud83d\ude00 <&'/\"}")]
    [InlineData("""["\"\\\b\f\n\r\t\u0001\u001f"]""", """["\"\\\b\f\n\r\t\u0001\u001f"]""")]
    [InlineData("1e-7", "1e-7")]
    public void Stringify_MatchesJavaScriptJsonStringifyOfTheParsedValue(string json, string expected)
        => Assert.Equal(expected, EcmaScriptJsonSerializer.Stringify(JsonNode.Parse(json)));

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void Stringify_EscapesALoneSurrogate(int codeUnit)
    {
        var value = "x" + (char)codeUnit + "y";

        Assert.Equal($"\"x\\u{codeUnit:x4}y\"", EcmaScriptJsonSerializer.Stringify(JsonValue.Create(value)));
    }

    [UnitTest]
    public void Stringify_KeepsASurrogatePairAndEscapesAReversedOne()
    {
        var value = "\ud83d\ude00\ude00\ud83d";

        Assert.Equal("\"\ud83d\ude00\\ude00\\ud83d\"", EcmaScriptJsonSerializer.Stringify(JsonValue.Create(value)));
    }
}
