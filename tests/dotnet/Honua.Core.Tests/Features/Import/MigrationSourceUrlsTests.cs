// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationSourceUrlsTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("https://other.example/items?offset=2#page", "https://other.example/items?offset=2#page")]
    [InlineData("items?offset=2", "https://demo.example/ogcapi/items?offset=2")]
    [InlineData("../items", "https://demo.example/items")]
    [InlineData("/items", "https://demo.example/items")]
    [InlineData(" /collections/roads/items?offset=100", "https://demo.example/collections/roads/items?offset=100")]
    [InlineData("//other.example/items", "https://other.example/items")]
    [InlineData("?offset=2", "https://demo.example/ogcapi/?offset=2")]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("http://[invalid", null)]
    public void ResolveUri_PreservesDirectoryRelativeAndAbsoluteLinks(string href, string? expected)
    {
        var resolved = MigrationSourceUrls.ResolveUri(href, new Uri("https://demo.example/ogcapi?token=secret#old"));

        (resolved?.AbsoluteUri).Should().Be(expected);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("https://demo.example/ogcapi", "collections/roads")]
    [InlineData("https://demo.example/ogcapi/?token=secret#old", "/collections/roads")]
    public void AppendPath_UsesDirectoryBaseAndDropsSourceQuery(string source, string path)
    {
        MigrationSourceUrls.AppendPath(new Uri(source), path).AbsoluteUri
            .Should().Be("https://demo.example/ogcapi/collections/roads");
    }
}
