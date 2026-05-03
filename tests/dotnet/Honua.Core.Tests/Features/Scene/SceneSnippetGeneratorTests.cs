// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene;

/// <summary>
/// Unit tests for the snippet generator that produces CesiumJS and
/// <c>&lt;honua-scene&gt;</c> embed snippets.
/// </summary>
public class SceneSnippetGeneratorTests
{
    [UnitTest]
    public void BuildCesiumJs_EmitsExpectedShape()
    {
        const string url = "https://server.honua.io/scenes/alpha/tileset.json";

        var snippet = SceneSnippetGenerator.BuildCesiumJs(url);

        Assert.Equal($"new Cesium.Cesium3DTileset({{ url: \"{url}\" }})", snippet);
    }

    [UnitTest]
    public void BuildHonuaSceneTag_EmitsExpectedShape()
    {
        const string url = "https://server.honua.io/scenes/alpha/tileset.json";

        var snippet = SceneSnippetGenerator.BuildHonuaSceneTag(url);

        Assert.Equal($"<honua-scene src=\"{url}\"></honua-scene>", snippet);
    }

    [UnitTest]
    public void BuildCesiumJs_EscapesQuotesAndScriptTags()
    {
        const string injectingUrl = "https://server.honua.io/scenes/\"</script>alert/tileset.json";

        var snippet = SceneSnippetGenerator.BuildCesiumJs(injectingUrl);

        Assert.DoesNotContain("</script>", snippet, StringComparison.Ordinal);
        Assert.Contains("\\\"", snippet, StringComparison.Ordinal);
        Assert.Contains("\\u003C", snippet, StringComparison.Ordinal);
    }

    [UnitTest]
    public void BuildHonuaSceneTag_EscapesAttributeContext()
    {
        const string injectingUrl = "https://server.honua.io/scenes/a\"&<>x/tileset.json";

        var snippet = SceneSnippetGenerator.BuildHonuaSceneTag(injectingUrl);

        Assert.DoesNotContain("\"&", snippet, StringComparison.Ordinal);
        Assert.Contains("&amp;", snippet, StringComparison.Ordinal);
        Assert.Contains("&quot;", snippet, StringComparison.Ordinal);
        Assert.Contains("&lt;", snippet, StringComparison.Ordinal);
        Assert.Contains("&gt;", snippet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BuildCesiumJs_RejectsBlankUrl(string? url)
    {
        Assert.ThrowsAny<ArgumentException>(() => SceneSnippetGenerator.BuildCesiumJs(url!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BuildHonuaSceneTag_RejectsBlankUrl(string? url)
    {
        Assert.ThrowsAny<ArgumentException>(() => SceneSnippetGenerator.BuildHonuaSceneTag(url!));
    }
}
