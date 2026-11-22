// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Scene.Catalog;
using Honua.Core.Features.Scene.Domain;
using Honua.Protocols.Scene;
using Honua.Protocols.Scene.Models;
using Honua.Scene;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Scene;

[Protocol(TestProtocols.Scene)]
public sealed class SceneDiscoveryContractDriftTests
{
    private const string BaseUrl = "https://maps.example.test";
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void LegacyHostedTiles_MapsToThreeDTilesAndUnclassified_NeverGuessesMesh()
    {
        var record = new SceneDatasetRecord
        {
            Id = "legacy-scene",
            Name = "Legacy Scene",
            AssetRoot = "/data/legacy",
            DatasetType = SceneDatasetType.HostedTiles,
            CreatedBy = "tester"
        };

        SceneServingProjection.ResolveServingFormat(record).Should().Be(SceneServingFormat.ThreeDTiles);
        SceneServingProjection.ResolveContentKind(record).Should().Be(SceneContentKind.Unclassified);
        I3sLayerTypeMap.TryToLayerType(SceneContentKind.Unclassified, out _).Should().BeFalse();
        I3sLayerTypeMap.TryToStoreProfile(SceneContentKind.Unclassified, out _).Should().BeFalse();
    }

    [UnitTest]
    public void ServingFormatAndContentKind_RoundTripAllCanonicalValues()
    {
        foreach (var format in Enum.GetValues<SceneServingFormat>())
        {
            var wire = SceneServingProjection.ToWireValue(format);
            SceneServingProjection.TryParseServingFormat(wire, out var parsed).Should().BeTrue();
            parsed.Should().Be(format);
        }

        foreach (var kind in Enum.GetValues<SceneContentKind>())
        {
            var wire = SceneServingProjection.ToWireValue(kind);
            SceneServingProjection.TryParseContentKind(wire, out var parsed).Should().BeTrue();
            parsed.Should().Be(kind);
        }

        SceneServingProjection.TryParseServingFormat("3dtiles", out var aliasFormat).Should().BeTrue();
        aliasFormat.Should().Be(SceneServingFormat.ThreeDTiles);
        SceneServingProjection.TryParseContentKind("3d-object", out var aliasKind).Should().BeTrue();
        aliasKind.Should().Be(SceneContentKind.Mesh);
    }

    [UnitTest]
    public void PublicSceneList_MatchesGoldenContractFixture()
    {
        var list = new PublicSceneListResponse
        {
            Scenes =
            [
                SceneDiscoveryEndpoints.ToSummary(SceneCatalog.ToEntry(BuildBuildingsRecord()), BaseUrl),
                SceneDiscoveryEndpoints.ToSummary(SceneCatalog.ToEntry(BuildCopcRecord()), BaseUrl)
            ]
        };

        var actual = JsonSerializer.Serialize(list, PublicSceneDiscoveryJsonContext.Default.PublicSceneListResponse);
        AssertJsonMatchesFixture(actual, "scene-list.v1.json");
    }

    [UnitTest]
    public void PublicSceneMetadata_MatchesGoldenContractFixture()
    {
        var metadata = SceneDiscoveryEndpoints.ToMetadata(SceneCatalog.ToEntry(BuildBuildingsRecord()), BaseUrl);
        var actual = JsonSerializer.Serialize(metadata, PublicSceneDiscoveryJsonContext.Default.PublicSceneMetadata);
        AssertJsonMatchesFixture(actual, "scene-metadata.v1.json");
    }

    [UnitTest]
    public void PublicSceneResolve_Public3DTiles_MatchesGoldenContractFixture()
    {
        var resolution = SceneDiscoveryEndpoints.ToResolution(SceneCatalog.ToEntry(BuildBuildingsRecord()), BaseUrl);
        var actual = JsonSerializer.Serialize(resolution, PublicSceneDiscoveryJsonContext.Default.PublicSceneResolution);
        AssertJsonMatchesFixture(actual, "scene-resolve-public.v1.json");
    }

    [UnitTest]
    public void PublicSceneResolve_ProtectedScene_MatchesGoldenContractFixtureWithoutCredentials()
    {
        var record = new SceneDatasetRecord
        {
            Id = "protected-campus",
            Name = "Protected Campus",
            AssetRoot = "/data/campus",
            ServingFormat = SceneServingFormat.ThreeDTiles,
            ContentKind = SceneContentKind.Mesh,
            IsPublic = false,
            RequiresAuth = true,
            AllowedRoles = ["campus-viewer"],
            CreatedAt = FixedTimestamp,
            CreatedBy = "tester"
        };

        var resolution = SceneDiscoveryEndpoints.ToResolution(SceneCatalog.ToEntry(record), BaseUrl);
        var actual = JsonSerializer.Serialize(resolution, PublicSceneDiscoveryJsonContext.Default.PublicSceneResolution);
        AssertJsonMatchesFixture(actual, "scene-resolve-protected.v1.json");
        actual.Should().NotContain("campus-viewer");
        actual.Should().NotContain("token=");
    }

    [UnitTest]
    public void PublicSceneDiscovery_RegistryRecordWithoutDescription_IsDiscoverable()
    {
        var record = BuildCopcRecord();
        var entry = SceneCatalog.ToEntry(record);

        SceneDiscoveryEndpoints.ToSummary(entry, BaseUrl).Description.Should().BeNull();
        SceneDiscoveryEndpoints.ToMetadata(entry, BaseUrl).Description.Should().BeNull();
        SceneDiscoveryEndpoints.ToResolution(entry, BaseUrl).SceneId.Should().Be(record.Id);
    }

    [UnitTest]
    public void PublicSceneResolve_CopcScene_MatchesGoldenContractFixture()
    {
        var resolution = SceneDiscoveryEndpoints.ToResolution(SceneCatalog.ToEntry(BuildCopcRecord()), BaseUrl);
        var actual = JsonSerializer.Serialize(resolution, PublicSceneDiscoveryJsonContext.Default.PublicSceneResolution);
        AssertJsonMatchesFixture(actual, "scene-resolve-copc.v1.json");
    }

    [UnitTest]
    public void SceneAccessEnvelope_MatchesVersionedCredentialTransportContractFixture()
    {
        var time = new FixedTimeProvider(FixedTimestamp);
        var options = new StaticOptionsMonitor(new SceneAccessSigningOptions
        {
            SigningKey = "contract-test-signing-key-9xQp4Lm8Vz2W",
            TokenTtlMinutes = 15,
            RefreshAfterFractionOfTtl = 0.5
        });
        var service = new SceneAccessEnvelopeService(options, time);

        var envelope = service.Issue("protected-campus", BaseUrl);
        envelope.Token.Should().NotBeNullOrWhiteSpace();
        envelope.ToString().Should().NotContain(envelope.Token);

        var normalized = envelope with { Token = "<opaque-token>" };
        var actual = JsonSerializer.Serialize(normalized, SceneAccessEnvelopeJsonContext.Default.SceneAccessEnvelope);
        AssertJsonMatchesFixture(actual, "scene-access-envelope.v1.json");
    }

    [UnitTest]
    public void SceneAccessEnvelope_UserInfoBaseUrl_OmitsAssetScopes()
    {
        var options = new StaticOptionsMonitor(new SceneAccessSigningOptions
        {
            SigningKey = "contract-test-signing-key-9xQp4Lm8Vz2W",
            TokenTtlMinutes = 15,
            RefreshAfterFractionOfTtl = 0.5
        });
        var service = new SceneAccessEnvelopeService(options, new FixedTimeProvider(FixedTimestamp));

        var envelope = service.Issue("protected-campus", "https://user:pass@maps.example.test");
        envelope.AssetScopes.Should().BeNull();
        envelope.Transports.Should().HaveCount(2);
    }

    private static SceneDatasetRecord BuildBuildingsRecord() => new()
    {
        Id = "downtown-buildings",
        Name = "Downtown Buildings",
        Description = "CityGML building footprints.",
        AssetRoot = "/data/downtown",
        ServingFormat = SceneServingFormat.ThreeDTiles,
        ContentKind = SceneContentKind.Buildings,
        Extent = new SceneExtent(-157.87, 21.29, -157.84, 21.32),
        IsPublic = true,
        RequiresAuth = false,
        CreatedAt = FixedTimestamp,
        CreatedBy = "tester"
    };

    private static SceneDatasetRecord BuildCopcRecord() => new()
    {
        Id = "lidar-harbor",
        Name = "Harbor LiDAR",
        AssetRoot = "/data/lidar",
        TilesetFileName = "harbor.copc.laz",
        DatasetType = SceneDatasetType.PointCloud,
        ServingFormat = SceneServingFormat.Copc,
        ContentKind = SceneContentKind.PointCloud,
        IsPublic = false,
        RequiresAuth = true,
        CreatedAt = FixedTimestamp,
        CreatedBy = "tester"
    };

    private static void AssertJsonMatchesFixture(string actualJson, string fixtureFileName)
    {
        var path = RepositoryPaths.Resolve("tests", "fixtures", "scenes", "contracts", fixtureFileName);
        var expectedNode = JsonNode.Parse(File.ReadAllText(path));
        var actualNode = JsonNode.Parse(actualJson);

        JsonNode.DeepEquals(actualNode, expectedNode).Should().BeTrue(
            $"serialized output drifted from golden fixture '{fixtureFileName}'.\nActual:\n{actualNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticOptionsMonitor(SceneAccessSigningOptions value) : IOptionsMonitor<SceneAccessSigningOptions>
    {
        public SceneAccessSigningOptions CurrentValue => value;
        public SceneAccessSigningOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SceneAccessSigningOptions, string?> listener) => null;
    }
}
