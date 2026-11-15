// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Protocols.Scene.ComponentTests;

[Protocol(ProtocolNames.Scene)]
public sealed class SceneGenerationETagTests
{
    [IntegrationTheory]
    [Operation(Operations.GetTileMetadata, Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    [InlineData("tileset.json")]
    [InlineData("tiles/0.b3dm")]
    public async Task GetAsset_GenerationChangesWithIdenticalContent_ReturnsNewValidator(string asset)
    {
        var source = new GenerationSource();
        await using var host = await SceneComponentTests.StartAsync(generations: source);
        var path = $"/scenes/public/{asset}";
        using var first = await host.Client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await host.Client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);

        source.Generation++;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var changed = await host.Client.SendAsync(request);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        changed.Headers.ETag.Should().NotBe(first.Headers.ETag);
        changed.Headers.ETag!.Tag.Should().EndWith("-g8\"");
        (await changed.Content.ReadAsByteArrayAsync()).Should().Equal(await first.Content.ReadAsByteArrayAsync());
    }

    private sealed class GenerationSource : IConfigurationGenerationSource
    {
        internal long Generation { get; set; } = 7;

        public Task<long> GetGenerationAsync(CancellationToken cancellationToken) => Task.FromResult(Generation);
    }
}
