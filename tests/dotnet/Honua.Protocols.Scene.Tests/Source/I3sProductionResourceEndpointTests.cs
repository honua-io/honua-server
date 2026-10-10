// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>Decodes actual persisted assets through the production I3S registration.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class I3sProductionResourceEndpointTests : IAsyncLifetime
{
    private const string Key = "i3s-production-resource-test";
    private const string Base = "/rest/services/i3s-persisted/SceneServer/layers/0";
    private readonly WebAppFixture _fixture;
    private readonly string _root;

    public I3sProductionResourceEndpointTests()
    {
        _root = Directory.CreateTempSubdirectory("honua-i3s-production-").FullName;
        var source = SceneFixtureRoots.Resolve("i3s-production");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Join(_root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        _fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", Key);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenes:Datasets:0:Id"] = "i3s-persisted",
                ["Scenes:Datasets:0:Name"] = "Persisted geometry",
                ["Scenes:Datasets:0:AssetRoot"] = _root,
                ["Scenes:Datasets:1:Id"] = "i3s-persisted-protected",
                ["Scenes:Datasets:1:Name"] = "Protected persisted geometry",
                ["Scenes:Datasets:1:AssetRoot"] = _root,
                ["Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",
            }));
        }).ConfigureServices(services =>
        {
            var license = new TestLicenseEntitlementService(HonuaEdition.Enterprise);
            services.AddSingleton<ILicenseEntitlementService>(license);
            services.AddSingleton<ILicenseStatusProvider>(license);
        });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task PersistedB3dmAndGlb_AllAdvertisedNodes_DecodeToSourceGeometryAndAttributes()
    {
        using var layer = await GetJsonAsync(Base);
        layer.RootElement.GetProperty("store").GetProperty("normalReferenceFrame").GetString().Should().Be("earth-centered");
        layer.RootElement.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("name").GetString())
            .Should().BeEquivalentTo("OBJECTID", "height", "name");
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var nodes = page.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(3);
        nodes[0].TryGetProperty("mesh", out _).Should().BeFalse("I3S 3DObject root is structural");
        nodes[0].GetProperty("children").EnumerateArray().Select(x => x.GetInt32()).Should().Equal(1, 2);
        for (var nodeIndex = 1; nodeIndex <= 2; nodeIndex++)
        {
            var node = nodes[nodeIndex];
            var mesh = node.GetProperty("mesh");
            var resource = mesh.GetProperty("geometry").GetProperty("resource").GetInt32();
            mesh.GetProperty("geometry").GetProperty("vertexCount").GetInt32().Should().Be(6);
            mesh.GetProperty("geometry").GetProperty("featureCount").GetInt32().Should().Be(2);
            var response = await _fixture.Client.GetAsync($"{Base}/nodes/{resource}/geometries/0");
            response.EnsureSuccessStatusCode();
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be(6);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)).Should().Be(2);
            AssertSourcePlacement(bytes, node.GetProperty("obb").GetProperty("center"), nodeIndex == 1 ? 0 : 150);
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8 + 6 * 44)).Should().Be(42);
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8 + 6 * 44 + 8)).Should().Be(77);
            bytes.Length.Should().Be(8 + 6 * 44 + 2 * 16);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + 6 * 44 + 16)).Should().Be(0);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + 6 * 44 + 20)).Should().Be(0);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + 6 * 44 + 24)).Should().Be(1);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + 6 * 44 + 28)).Should().Be(1);
            var fields = layer.RootElement.GetProperty("attributeStorageInfo");
            foreach (var field in fields.EnumerateArray())
            {
                var payload = await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/{resource}/attributes/{field.GetProperty("key").GetString()}/0");
                BinaryPrimitives.ReadUInt32LittleEndian(payload).Should().Be(2);
                switch (field.GetProperty("name").GetString())
                {
                    case "OBJECTID":
                        payload.Length.Should().Be(12);
                        BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)).Should().Be(42);
                        BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)).Should().Be(77);
                        break;
                    case "height":
                        BinaryPrimitives.ReadDoubleLittleEndian(payload.AsSpan(8)).Should().Be(50);
                        BinaryPrimitives.ReadDoubleLittleEndian(payload.AsSpan(16)).Should().Be(35);
                        break;
                    case "name":
                        var firstLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8));
                        Encoding.UTF8.GetString(payload, 16, firstLength - 1).Should().Be("Honolulu café");
                        Encoding.UTF8.GetString(payload, 16 + firstLength, payload.Length - 16 - firstLength - 1).Should().Be("Second building");
                        break;
                }
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/textures/{textureId}")]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/textures/{textureId}")]
    public async Task TexturedNode_AdvertisedMaterial_FetchesOriginalPng_AndUntexturedNodeHasNoTexture()
    {
        using var layer = await GetJsonAsync(Base);
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var definition = page.RootElement.GetProperty("nodes")[2].GetProperty("mesh").GetProperty("material").GetProperty("definition").GetInt32();
        layer.RootElement.GetProperty("materialDefinitions")[definition].GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture")
            .GetProperty("textureSetDefinitionId").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        var response = await _fixture.Client.GetAsync(Base + "/nodes/2/textures/0");
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var oracle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(_root, "oracle.json")));
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(oracle.RootElement.GetProperty("texture_sha256").GetString());
        bytes.Take(8).Should().Equal(137, 80, 78, 71, 13, 10, 26, 10);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)).Should().Be(2);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)).Should().Be(2);
        var alias = await _fixture.Client.GetByteArrayAsync("/scenes/i3s-persisted/SceneServer/layers/0/nodes/2/textures/0");
        alias.Should().Equal(bytes);
        var absent = await _fixture.Client.GetAsync(Base + "/nodes/1/textures/0");
        await absent.AssertGeoServicesErrorAsync(404, 404);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/textures/{textureId}")]
    public async Task ProtectedResources_AnonymousDenied_AuthenticatedBytesMatchPublicScene()
    {
        const string protectedBase = "/rest/services/i3s-persisted-protected/SceneServer/layers/0";
        using var authenticated = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", Key));
        foreach (var suffix in new[] { "/nodepages/0", "/nodes/1/geometries/0", "/nodes/1/attributes/f_0/0", "/nodes/2/textures/0" })
        {
            var denied = await _fixture.Client.GetAsync(protectedBase + suffix);
            await denied.AssertGeoServicesErrorAsync(401, 499);
            var bytes = await authenticated.GetByteArrayAsync(protectedBase + suffix);
            bytes.Should().Equal(await _fixture.Client.GetByteArrayAsync(Base + suffix));
        }
    }

    [IntegrationTheory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    [InlineData("truncated-container")]
    [InlineData("external-content")]
    [InlineData("external-texture")]
    [InlineData("bad-texture-checksum")]
    [InlineData("invalid-base64")]
    [InlineData("metadata-normalized")]
    [InlineData("metadata-scale-offset")]
    [InlineData("metadata-nodata-default")]
    [InlineData("reflective-transform")]
    [InlineData("multimaterial-replacement")]
    public async Task InvalidPersistedContent_DoesNotAdvertiseOrServePartialResources(string invalid)
    {
        if (invalid == "external-content")
        {
            var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
            tileset["root"]!["children"]![1]!["content"]!["uri"] = "../outside.glb";
            await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        }
        else if (invalid == "multimaterial-replacement")
        {
            var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
            var parent = tileset["root"]!["children"]![0]!;
            parent["content"]!["uri"] = "tiles/multi.glb";
            parent["children"] = new JsonArray(new JsonObject
            {
                ["boundingVolume"] = tileset["root"]!["boundingVolume"]!.DeepClone(),
                ["geometricError"] = 0,
                ["content"] = new JsonObject { ["uri"] = "tiles/1.glb" },
            });
            await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        }
        else if (invalid == "truncated-container")
        {
            await File.WriteAllBytesAsync(Path.Join(_root, "tiles/1.glb"), "glTF"u8.ToArray());
        }
        else if (invalid == "bad-texture-checksum")
        {
            var bytes = await File.ReadAllBytesAsync(Path.Join(_root, "tiles/1.glb"));
            var png = bytes.AsSpan().IndexOf(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            png.Should().BeGreaterThan(0);
            bytes[png + 29] ^= 1;
            await File.WriteAllBytesAsync(Path.Join(_root, "tiles/1.glb"), bytes);
        }
        else if (invalid.StartsWith("metadata-", StringComparison.Ordinal))
        {
            await RewriteGlbAsync("tiles/1.glb", json =>
            {
                var property = json["extensions"]!["EXT_structural_metadata"]!["schema"]!["classes"]!["building"]!["properties"]!["height"]!;
                if (invalid == "metadata-normalized") { property["normalized"] = true; }
                else if (invalid == "metadata-scale-offset") { property["scale"] = 2; property["offset"] = 10; }
                else { property["noData"] = 50; property["default"] = 99; }
            });
        }
        else if (invalid == "reflective-transform")
        {
            await RewriteGlbAsync("tiles/1.glb", json => json["nodes"]![0]!["scale"] = new JsonArray(-1, 1, 1));
        }
        else
        {
            await RewriteGlbAsync("tiles/1.glb", json =>
            {
                json["images"]![0]!.AsObject().Remove("bufferView");
                json["images"]![0]!["uri"] = invalid == "invalid-base64" ? "data:image/png;base64,!!!" : "https://example.invalid/private.png";
            });
        }

        using var layer = await GetJsonAsync(Base);
        layer.RootElement.TryGetProperty("nodePages", out _).Should().BeFalse();
        layer.RootElement.GetProperty("geometryDefinitions").GetArrayLength().Should().Be(0);
        layer.RootElement.GetProperty("textureSetDefinitions").GetArrayLength().Should().Be(0);
        foreach (var resource in new[] { "/nodepages/0", "/nodes/1/geometries/0", "/nodes/1/attributes/f_0/0", "/nodes/2/textures/0" })
        {
            var response = await _fixture.Client.GetAsync(Base + resource);
            await response.AssertGeoServicesErrorAsync(404, 404);
        }
    }

    [IntegrationTheory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    [InlineData(false, "back")]
    [InlineData(true, "none")]
    public async Task SourceMaterialCulling_PreservesGltfSingleAndDoubleSidedRendering(bool doubleSided, string cullFace)
    {
        await RewriteGlbAsync("tiles/1.glb", json => json["materials"]![0]!["doubleSided"] = doubleSided);
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var definition = page.RootElement.GetProperty("nodes")[2].GetProperty("mesh").GetProperty("material").GetProperty("definition").GetInt32();
        using var layer = await GetJsonAsync(Base);
        var material = layer.RootElement.GetProperty("materialDefinitions")[definition];
        material.GetProperty("doubleSided").GetBoolean().Should().Be(doubleSided);
        material.GetProperty("cullFace").GetString().Should().Be(cullFace);
        (await _fixture.Client.GetByteArrayAsync(Base + "/nodes/2/geometries/0")).Length.Should().Be(8 + 6 * 44 + 2 * 16);
    }

    [IntegrationTheory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    [InlineData("REPLACE", 20d, false)]
    [InlineData("ADD", 20d, true)]
    [InlineData("REPLACE", 0d, false)]
    public async Task PersistedHierarchy_UsesPixelAreaAndPreservesRefinement(string refinement, double geometricError, bool multipleMaterials)
    {
        var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
        var root = tileset["root"]!;
        root["refine"] = refinement;
        root["children"] = new JsonArray(new JsonObject
        {
            ["boundingVolume"] = root["boundingVolume"]!.DeepClone(),
            ["geometricError"] = geometricError,
            ["content"] = new JsonObject { ["uri"] = multipleMaterials ? "tiles/multi.glb" : "tiles/1.glb" },
            ["children"] = new JsonArray(new JsonObject
            {
                ["boundingVolume"] = root["boundingVolume"]!.DeepClone(),
                ["geometricError"] = 5,
                ["refine"] = "REPLACE",
                ["content"] = new JsonObject { ["uri"] = "tiles/1.glb" },
                ["children"] = new JsonArray(new JsonObject
                {
                    ["boundingVolume"] = root["boundingVolume"]!.DeepClone(),
                    ["geometricError"] = 0,
                    ["content"] = new JsonObject { ["uri"] = "tiles/0.b3dm" },
                }),
            }),
        });
        await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        using var layer = await GetJsonAsync(Base);
        layer.RootElement.GetProperty("nodePages").GetProperty("lodSelectionMetricType").GetString().Should().Be("maxScreenThresholdSQ");
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var nodes = page.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(refinement == "REPLACE" ? 4 : 6);
        foreach (var node in nodes.EnumerateArray().Where(node => node.TryGetProperty("mesh", out _)))
        {
            var resource = node.GetProperty("mesh").GetProperty("geometry").GetProperty("resource").GetInt32();
            var bytes = await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/{resource}/geometries/0");
            BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be(multipleMaterials && (resource is 2 or 3) ? 3u : 6u);
        }

        if (refinement == "REPLACE")
        {
            var radiusSquared = nodes[1].GetProperty("obb").GetProperty("halfSize").EnumerateArray().Sum(value => value.GetDouble() * value.GetDouble());
            var expectedArea = geometricError == 0 ? double.MaxValue : Math.PI * radiusSquared * 16 * 16 / (geometricError * geometricError);
            nodes[1].GetProperty("lodThreshold").GetDouble().Should().Be(expectedArea);
            SelectedResources(0.32).Should().Equal(1);
            if (geometricError == 0)
            {
                SelectedResources(20.48).Should().Equal(new[] { 1 }, "zero-error source content already satisfies the SSE target");
            }
            else
            {
                SelectedResources(1.28).Should().Equal(2);
                SelectedResources(20.48).Should().Equal(3);
            }
        }
        else
        {
            SelectedResources(0.32).Should().Equal(2, 3, 4);
            SelectedResources(20.48).Should().Equal(new[] { 2, 3, 5 }, "additive parent materials remain when descendants refine");
        }

        int[] SelectedResources(double focalLengthSquaredOverDistanceSquared)
        {
            var result = new List<int>();
            Visit(0);
            return result.ToArray();
            void Visit(int index)
            {
                var node = nodes[index];
                var children = node.GetProperty("children").EnumerateArray().Select(child => child.GetInt32()).ToArray();
                var radiusSquared = node.GetProperty("obb").GetProperty("halfSize").EnumerateArray().Sum(value => value.GetDouble() * value.GetDouble());
                var projectedArea = Math.PI * radiusSquared * focalLengthSquaredOverDistanceSquared;
                var hasMesh = node.TryGetProperty("mesh", out var mesh);
                if (children.Length > 0 && (!hasMesh || projectedArea > node.GetProperty("lodThreshold").GetDouble()))
                {
                    foreach (var child in children) { Visit(child); }
                }
                else if (hasMesh)
                {
                    result.Add(mesh.GetProperty("geometry").GetProperty("resource").GetInt32());
                }
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    public async Task MultipleSourceMaterials_EveryTriangleHasItsOwnPublishedMaterialAndResources()
    {
        var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
        tileset["root"]!["children"]![1]!["content"]!["uri"] = "tiles/multi.glb";
        await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var nodes = page.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(5);
        nodes[2].TryGetProperty("mesh", out _).Should().BeFalse();
        nodes[2].GetProperty("children").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(3, 4);
        using var layer = await GetJsonAsync(Base);
        for (var index = 3; index <= 4; index++)
        {
            var mesh = nodes[index].GetProperty("mesh");
            var geometry = await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/{index}/geometries/0");
            BinaryPrimitives.ReadUInt32LittleEndian(geometry).Should().Be(3);
            BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(4)).Should().Be(1);
            var attributes = await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/{index}/attributes/f_0/0");
            BinaryPrimitives.ReadUInt32LittleEndian(attributes).Should().Be(1);
            BinaryPrimitives.ReadInt32LittleEndian(attributes.AsSpan(4)).Should().Be(index == 3 ? 42 : 77);
            var material = layer.RootElement.GetProperty("materialDefinitions")[mesh.GetProperty("material").GetProperty("definition").GetInt32()];
            if (index == 3)
            {
                material.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _).Should().BeTrue();
                (await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/{index}/textures/0")).Length.Should().BeGreaterThan(32);
            }
            else
            {
                material.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _).Should().BeFalse();
                material.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor").EnumerateArray().Select(value => value.GetDouble())
                    .Should().Equal(0, 0, 1, 1);
                await (await _fixture.Client.GetAsync($"{Base}/nodes/{index}/textures/0")).AssertGeoServicesErrorAsync(404, 404);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task SourceRevisionChange_InvalidatesPreviouslyDerivedDescriptorsAndBytes()
    {
        var before = await _fixture.Client.GetByteArrayAsync(Base + "/nodes/2/geometries/0");
        await RewriteGlbAsync("tiles/1.glb", json => json["nodes"]![0]!["translation"] = new JsonArray(50, 0, 0));
        var after = await _fixture.Client.GetByteArrayAsync(Base + "/nodes/2/geometries/0");
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var center = page.RootElement.GetProperty("nodes")[2].GetProperty("obb").GetProperty("center");
        // glTF X is east in this fixture: the changed node translation adds50m.
        AssertSourcePlacement(after, center, 200);
        after.Should().NotEqual(before);
        var provider = _fixture.Services.GetRequiredService<II3sSceneResourceProvider>();
        var noStore = new SceneDataset { Id = "no-store", Name = "No store", AssetRoot = _root, CachePolicy = new(3600, true) };
        var first = await provider.GetResourcesAsync(noStore, CancellationToken.None);
        var second = await provider.GetResourcesAsync(noStore, CancellationToken.None);
        first.Should().NotBeNull();
        second.Should().NotBeSameAs(first, "NoStore bypasses the shared derived cache");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task PersistedStringAttributes_NullAndEmptyRemainDistinctInDeclaredBinaryLayout()
    {
        var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
        tileset["root"]!["children"]!.AsArray().RemoveAt(1);
        tileset["root"]!["children"]![0]!["content"]!["uri"] = "tiles/null.b3dm";
        await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        using var layer = await GetJsonAsync(Base);
        var field = layer.RootElement.GetProperty("attributeStorageInfo").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "name");
        field.GetProperty("attributeValues").GetProperty("valueType").GetString().Should().Be("String");
        var bytes = await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/1/attributes/{field.GetProperty("key").GetString()}/0");
        bytes.Length.Should().Be(17);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be(2);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)).Should().Be(1);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)).Should().Be(0, "null has no encoded string bytes");
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)).Should().Be(1, "empty string has a terminating NUL");
        bytes[16].Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task ExistingHonuaGeneratedGlb_UsesPersistedEcefTranslationWithoutAnExtraAxisRotation()
    {
        var source = new[]
        {
            new SceneVertex(-157.8581, 21.3069, 20), new SceneVertex(-157.8579, 21.3069, 20),
            new SceneVertex(-157.8581, 21.3071, 20), new SceneVertex(-157.8581, 21.3069, 20),
        };
        var glb = GeometryTileBuilder.BuildGlb([new SceneFeature
        {
            Id = 47, Geometry = new() { Kind = SceneGeometryKind.Polygon, Vertices = source },
            Attributes = new Dictionary<string, object?> { ["OBJECTID"] = 47, ["name"] = "generated source" },
        }], [new SceneAttributeSchema { PropertyId = "OBJECTID", FieldName = "OBJECTID", SchemaType = "SCALAR", SchemaComponentType = "INT32" },
            new SceneAttributeSchema { PropertyId = "name", FieldName = "name", SchemaType = "STRING" }], extrusion: null);
        await File.WriteAllBytesAsync(Path.Join(_root, "tiles/generated.glb"), glb);
        var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
        var root = tileset["root"]!.AsObject();
        root.Remove("transform");
        root.Remove("children");
        root["content"] = new JsonObject { ["uri"] = "tiles/generated.glb" };
        await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var nodes = page.RootElement.GetProperty("nodes");
        nodes[0].TryGetProperty("mesh", out _).Should().BeFalse();
        var center = nodes[1].GetProperty("obb").GetProperty("center");
        var bytes = await _fixture.Client.GetByteArrayAsync(Base + "/nodes/1/geometries/0");
        BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be(3);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)).Should().Be(1);
        for (var index = 0; index < 3; index++)
        {
            var longitude = center[0].GetDouble() + BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8 + index * 12));
            var latitude = center[1].GetDouble() + BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12 + index * 12));
            var height = center[2].GetDouble() + BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16 + index * 12));
            source.Take(3).Should().Contain(vertex => Math.Abs(vertex.Longitude - longitude) < 1e-8
                && Math.Abs(vertex.Latitude - latitude) < 1e-8 && Math.Abs(vertex.Height!.Value - height) < 0.02);
        }

        var objectId = await _fixture.Client.GetByteArrayAsync(Base + "/nodes/1/attributes/f_0/0");
        BinaryPrimitives.ReadInt32LittleEndian(objectId.AsSpan(4)).Should().Be(47);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task PersistedInt64Attributes_PreserveValuesBeyondDoublePrecision()
    {
        const long first = 9007199254740993;
        const long second = -9007199254740993;
        var tileset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(_root, "tileset.json")))!;
        tileset["root"]!["children"]!.AsArray().RemoveAt(0);
        await File.WriteAllTextAsync(Path.Join(_root, "tileset.json"), tileset.ToJsonString());
        await RewriteGlbAsync("tiles/1.glb", json =>
            json["extensions"]!["EXT_structural_metadata"]!["schema"]!["classes"]!["building"]!["properties"]!["height"]!["componentType"] = "INT64");
        var path = Path.Join(_root, "tiles/1.glb");
        var source = await File.ReadAllBytesAsync(path);
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(12));
        using (var metadata = JsonDocument.Parse(source.AsMemory(20, jsonLength)))
        {
            var table = metadata.RootElement.GetProperty("extensions").GetProperty("EXT_structural_metadata").GetProperty("propertyTables")[0];
            var viewIndex = table.GetProperty("properties").GetProperty("height").GetProperty("values").GetInt32();
            var offset = metadata.RootElement.GetProperty("bufferViews")[viewIndex].GetProperty("byteOffset").GetInt32();
            BinaryPrimitives.WriteInt64LittleEndian(source.AsSpan(28 + jsonLength + offset), first);
            BinaryPrimitives.WriteInt64LittleEndian(source.AsSpan(36 + jsonLength + offset), second);
        }
        await File.WriteAllBytesAsync(path, source);
        using var layer = await GetJsonAsync(Base);
        var declared = layer.RootElement.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "height");
        declared.GetProperty("type").GetString().Should().Be("esriFieldTypeBigInteger");
        var storage = layer.RootElement.GetProperty("attributeStorageInfo").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "height");
        storage.GetProperty("attributeValues").GetProperty("valueType").GetString().Should().Be("Int64");
        var bytes = await _fixture.Client.GetByteArrayAsync($"{Base}/nodes/1/attributes/{storage.GetProperty("key").GetString()}/0");
        bytes.Length.Should().Be(24);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be(2);
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)).Should().Be(first);
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)).Should().Be(second);
        using var page = await GetJsonAsync(Base + "/nodepages/0");
        var geometry = await _fixture.Client.GetByteArrayAsync(Base + "/nodes/1/geometries/0");
        AssertSourcePlacement(geometry, page.RootElement.GetProperty("nodes")[1].GetProperty("obb").GetProperty("center"), 150);
    }

    private async Task RewriteGlbAsync(string fileName, Action<JsonNode> change)
    {
        var path = Path.Join(_root, fileName);
        var bytes = await File.ReadAllBytesAsync(path);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var json = JsonNode.Parse(bytes.AsSpan(20, count))!;
        change(json);
        var encoded = Encoding.UTF8.GetBytes(json.ToJsonString());
        var paddedLength = (encoded.Length + 3) / 4 * 4;
        var output = new byte[20 + paddedLength + bytes.Length - 20 - count];
        bytes.AsSpan(0, 20).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)output.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)paddedLength);
        output.AsSpan(20, paddedLength).Fill(32);
        encoded.CopyTo(output, 20);
        bytes.AsSpan(20 + count).CopyTo(output.AsSpan(20 + paddedLength));
        await File.WriteAllBytesAsync(path, output);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        var response = await _fixture.Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync();
        var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "a successful descriptor must not be a GeoServices error envelope: {0}", Encoding.UTF8.GetString(body));
        return document;
    }

    private static void AssertSourcePlacement(byte[] bytes, JsonElement center, double eastOffset)
    {
        var source = new[] { (-20d, -20d, 10d), (20d, -20d, 10d), (0d, 20d, 50d), (50d, -20d, 10d), (90d, -20d, 10d), (70d, 20d, 35d) };
        var origin = EcefCoordinateTransform.ToEcef(-157.8581, 21.3069, 0);
        var lon = -157.8581 * Math.PI / 180;
        var lat = 21.3069 * Math.PI / 180;
        for (var i = 0; i < source.Length; i++)
        {
            var longitude = center[0].GetDouble() + BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8 + i * 12));
            var latitude = center[1].GetDouble() + BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12 + i * 12));
            var height = center[2].GetDouble() + BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16 + i * 12));
            var decoded = EcefCoordinateTransform.ToEcef(longitude, latitude, height);
            var (east, north, up) = source[i];
            east += eastOffset;
            var x = origin.X - east * Math.Sin(lon) - north * Math.Sin(lat) * Math.Cos(lon) + up * Math.Cos(lat) * Math.Cos(lon);
            var y = origin.Y + east * Math.Cos(lon) - north * Math.Sin(lat) * Math.Sin(lon) + up * Math.Cos(lat) * Math.Sin(lon);
            var z = origin.Z + north * Math.Cos(lat) + up * Math.Sin(lat);
            decoded.X.Should().BeApproximately(x, 0.03);
            decoded.Y.Should().BeApproximately(y, 0.03);
            decoded.Z.Should().BeApproximately(z, 0.03);
        }
    }
}
