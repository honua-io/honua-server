// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Reporting.Domain;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.TestKit.Attributes;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;

namespace Honua.Core.Tests.Features.Studio;

public sealed class MapAppVocabularyTests
{
    [UnitTest]
    public void ReportMapEmbed_SerializedReference_UsesCanonicalMapUri()
    {
        var section = new MapEmbedSection { Caption = "Map", MapUri = "honua://maps/map-stable" };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        var json = JsonSerializer.SerializeToElement(section, options);

        Assert.Equal("honua://maps/map-stable", json.GetProperty("mapUri").GetString());
        Assert.False(json.TryGetProperty("mapPackageUri", out _));
    }

    [UnitTest]
    public void Map_SerializedDraft_UsesCanonicalIdentity()
    {
        var map = Map.CreateDraft("map-stable", "honua_map.v1", [], []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(map, PackagingJsonContext.Default.Map));

        Assert.Equal("map-stable", json.RootElement.GetProperty("mapId").GetString());
        Assert.False(json.RootElement.TryGetProperty("mapPackageId", out _));
    }

    [UnitTest]
    public void App_SerializedDraft_PreservesCanonicalMapReference()
    {
        var app = App.CreateDraft("app-stable", "js", "honua_app.v1", mapId: "map-stable");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(app, PackagingJsonContext.Default.App));

        Assert.Equal("app-stable", json.RootElement.GetProperty("appId").GetString());
        Assert.Equal("map-stable", json.RootElement.GetProperty("mapId").GetString());
        Assert.False(json.RootElement.TryGetProperty("appPackageId", out _));
        Assert.False(json.RootElement.TryGetProperty("mapPackageId", out _));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(StudioPackageFamily.Map)]
    [InlineData(StudioPackageFamily.App)]
    public void StudioValidator_CanonicalBody_AcceptsCurrentFormat(StudioPackageFamily family)
    {
        Assert.Equal(StudioPackageValidationStatus.Valid, ValidateBody(family, BodyVocabulary.Current).Status);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(StudioPackageFamily.Map)]
    [InlineData(StudioPackageFamily.App)]
    public void StudioValidator_RetiredBody_RejectsNewWrites(StudioPackageFamily family)
    {
        Assert.Equal(StudioPackageValidationStatus.Invalid, ValidateBody(family, BodyVocabulary.Retired).Status);
    }

    private enum BodyVocabulary { Current, Retired }

    private static StudioValidationSummary ValidateBody(StudioPackageFamily family, BodyVocabulary vocabulary)
    {
        var body = family == StudioPackageFamily.App
            ? """{"appId":"app-stable","mapId":"map-stable","targetSdk":"js","format":"honua_app.v1","status":"Draft","createdAt":"2026-11-17T00:00:00Z"}"""
            : """{"mapId":"map-stable","format":"honua_map.v1","sourceBindings":[]}""";
        if (vocabulary == BodyVocabulary.Retired)
        {
            body = body.Replace("mapId", "mapPackageId", StringComparison.Ordinal)
                .Replace("appId", "appPackageId", StringComparison.Ordinal)
                .Replace("honua_map.v1", "honua_map_package.v1", StringComparison.Ordinal)
                .Replace("honua_app.v1", "honua_app_package.v1", StringComparison.Ordinal);
        }

        using var json = JsonDocument.Parse(body);
        var envelope = new StudioPackageEnvelope
        {
            Family = family,
            SchemaVersion = "1.0",
            Format = family == StudioPackageFamily.App ? "honua_app.v1" : "honua_map.v1",
            Body = json.RootElement.Clone()
        };
        var validator = new StudioPackageValidator(new StudioPackageFamilyRegistry(new InMemoryStudioPackageStore()), TimeProvider.System);
        return validator.Validate(envelope);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(StudioPackageFamily.Map, "map")]
    [InlineData(StudioPackageFamily.App, "app")]
    public void DeploymentSource_SerializedIdentity_PreservesSourceId(StudioPackageFamily family, string expectedKind)
    {
        var source = family == StudioPackageFamily.App
            ? DeploymentSource.FromApp("app-stable", "map-stable")
            : DeploymentSource.FromMap("map-stable");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(source, DeploymentJsonContext.Default.DeploymentSource));

        Assert.Equal(expectedKind, json.RootElement.GetProperty("kind").GetString());
        Assert.Equal(family == StudioPackageFamily.App ? "app-stable" : "map-stable", json.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal("map-stable", json.RootElement.GetProperty("mapId").GetString());
        Assert.False(json.RootElement.TryGetProperty("mapPackageId", out _));
        Assert.False(json.RootElement.TryGetProperty("appPackageId", out _));
    }
}
