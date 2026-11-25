// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Scene;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed class SceneAndTileGrpcHardeningTests
{
    [UnitTest]
    public async Task ListScenes_OffsetPlusMaxIntPageSize_DoesNotOverflow()
    {
        var datasets = new Dictionary<string, SceneDataset>(StringComparer.Ordinal)
        {
            ["scene-a"] = CreatePublicDataset("scene-a", "Scene A", "/tmp/scene-a"),
            ["scene-b"] = CreatePublicDataset("scene-b", "Scene B", "/tmp/scene-b")
        };
        var options = new SceneDatasetOptions
        {
            Datasets =
            [
                new SceneDatasetEntry { Id = "scene-a", Name = "Scene A", AssetRoot = "/tmp/scene-a" },
                new SceneDatasetEntry { Id = "scene-b", Name = "Scene B", AssetRoot = "/tmp/scene-b" }
            ]
        };
        var service = new HonuaSceneGrpcService(
            new DictionarySceneRegistry(datasets),
            Options.Create(options),
            NullLogger<HonuaSceneGrpcService>.Instance);

        var response = await service.ListScenes(
            new Proto.ListScenesRequest
            {
                PageToken = SceneCatalogPageToken.Encode(1),
                PageSize = int.MaxValue
            },
            CreateCallContext(CancellationToken.None));

        response.Scenes.Should().ContainSingle(scene => scene.SceneId == "scene-b");
        response.NextPageToken.Should().BeEmpty();
    }

    [UnitTest]
    public async Task StreamTiles_ForwardsCallCancellationTokenToResponseStreamWriteAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "honua-grpc-tile-ct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await WriteSampleTilesetAsync(tempRoot);
            var dataset = CreatePublicDataset("scene-stream", "Stream Scene", tempRoot) with { TilesetFileName = "tileset.json" };
            var service = new HonuaTileGrpcService(
                new DictionarySceneRegistry(new Dictionary<string, SceneDataset>(StringComparer.Ordinal) { ["scene-stream"] = dataset }),
                NullLogger<HonuaTileGrpcService>.Instance);

            using var cts = new CancellationTokenSource();
            var writer = new RecordingStreamWriter();

            await service.StreamTiles(
                new Proto.StreamTilesRequest { SceneId = "scene-stream" },
                writer,
                CreateCallContext(cts.Token));

            writer.WrittenCount.Should().Be(1);
            writer.ObservedTokens.Should().ContainSingle().Which.Should().Be(cts.Token);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static SceneDataset CreatePublicDataset(string id, string name, string assetRoot) => new()
    {
        Id = id,
        Name = name,
        AssetRoot = assetRoot,
        AccessPolicy = new AccessPolicy { AllowAnonymous = true }
    };

    private static async Task WriteSampleTilesetAsync(string tempRoot)
    {
        await File.WriteAllTextAsync(
            Path.Join(tempRoot, "tileset.json"),
            """{"asset":{"version":"1.1"},"geometricError":100,"root":{"boundingVolume":{"box":[0,0,0,1,0,0,0,1,0,0,0,1]},"geometricError":10,"content":{"uri":"root.glb"}}}""");
        await File.WriteAllBytesAsync(Path.Join(tempRoot, "root.glb"), [0x67, 0x6C, 0x54, 0x46]);
    }

    private static TestServerCallContext CreateCallContext(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new TestServerCallContext(cancellationToken);
        context.UserState["__HttpContext"] = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        return context;
    }

    private sealed class DictionarySceneRegistry(IReadOnlyDictionary<string, SceneDataset> datasets) : ISceneDatasetRegistry
    {
        public ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(datasets.TryGetValue(id, out var dataset) ? dataset : null);
    }

    private sealed class RecordingStreamWriter : IServerStreamWriter<Proto.Tile>
    {
        public int WrittenCount { get; private set; }
        public List<CancellationToken> ObservedTokens { get; } = [];
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Proto.Tile message)
        {
            WrittenCount++;
            ObservedTokens.Add(CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task WriteAsync(Proto.Tile message, CancellationToken cancellationToken)
        {
            WrittenCount++;
            ObservedTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class TestServerCallContext(CancellationToken cancellationToken) : ServerCallContext
    {
        private readonly Metadata _responseTrailers = new();

        protected override string MethodCore => "/geospatial.v1.SceneService/ListScenes";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => cancellationToken;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
