// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Semantic source-resolution regressions from the actual published-client map journey.
/// Persistence of a source identifier alone does not make a portable map binding.
/// </summary>
public sealed class StudioPackageValidatorSourceReferencesTests
{
    [Theory]
    [InlineData("\"sources\": {}")]
    [InlineData("\"sources\": {\"buffer\":{\"protocol\":\"honua-feature-service\",\"serviceId\":\"buffer-service\",\"layerId\":1}}")]
    [InlineData("\"mapSpec\": {\"sources\":{\"buffer\":null}}")]
    [InlineData("\"sourceBindings\": [{\"sourceId\":\"other\",\"protocol\":\"ogc_features\",\"locator\":{\"collectionId\":\"other\"}}]")]
    [Trait("Tier", "Fast")]
    public void Validate_LayerWithoutItsDeclaredSource_ReportsPreciseReference(string sourceMembers)
    {
        var summary = ValidateBody(LayerBody(sourceMembers));

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        var diagnostic = Assert.Single(summary.Diagnostics);
        Assert.Equal("studio.map.layer.source.unresolved", diagnostic.Code);
        Assert.Equal("/body/layers/0/sourceId", diagnostic.Path);
        Assert.Contains("sourceBindings", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sourceBindings\": [{\"sourceId\":\"buffer\",\"protocol\":\"geoservices_feature_service\",\"locator\":{\"serviceId\":\"buffer-service\",\"layerId\":1}}]")]
    [InlineData("\"mapSpec\": {\"version\":8,\"sources\":{\"buffer\":{\"type\":\"geojson\",\"data\":{\"type\":\"FeatureCollection\",\"features\":[]}}},\"layers\":[]}")]
    [Trait("Tier", "Fast")]
    public void Validate_LayerWithCanonicalSource_RemainsValid(string sourceMembers)
    {
        var summary = ValidateBody(LayerBody(sourceMembers));

        Assert.Empty(summary.Diagnostics);
        Assert.Equal(StudioPackageValidationStatus.Valid, summary.Status);
    }

    [UnitTest]
    public void Validate_EmptyMapDraft_RemainsValid()
    {
        var summary = ValidateBody("""{"mapPackageId":"empty","format":"honua_map_package.v1","layers":[]}""");

        Assert.Empty(summary.Diagnostics);
        Assert.Equal(StudioPackageValidationStatus.Valid, summary.Status);
    }

    [UnitTest]
    public async Task SaveDraftAsVersion_UnresolvedSourceRemainsInvalidInStoredVersion()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        using var provider = services.BuildServiceProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        using var document = JsonDocument.Parse(LayerBody("\"sources\": {}"));
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "unresolved-buffer",
            WorkspaceId = "studio",
            OwnerId = "author",
            ActorId = "author",
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Format = "honua_map_package.v1",
                Body = document.RootElement.Clone(),
            },
        });

        Assert.Equal(StudioPackageValidationStatus.Invalid, draft.Validation.Status);
        var version = await lifecycle.SaveDraftAsVersionAsync(draft.DraftId, "retain incomplete work", "author");

        var saved = Assert.IsType<StudioContentVersion>(version);
        Assert.Equal(StudioPackageValidationStatus.Invalid, saved.Validation.Status);
        Assert.Contains(saved.Validation.Diagnostics, diagnostic => diagnostic.Code == "studio.map.layer.source.unresolved");
        Assert.True(JsonElement.DeepEquals(document.RootElement, saved.Envelope.Body!.Value));
    }

    private static string LayerBody(string sourceMembers) =>
        """{"mapPackageId":"buffer-exploration","format":"honua_map_package.v1","layers":[{"id":"buffer-layer","sourceId":"buffer","type":"fill"}],"view":{"center":[0,0],"zoom":14},"""
        + sourceMembers + "}";

    private static StudioValidationSummary ValidateBody(string json)
    {
        using var document = JsonDocument.Parse(json);
        var validator = new StudioPackageValidator(
            new StudioPackageFamilyRegistry(new InMemoryStudioPackageStore()), TimeProvider.System);
        return validator.Validate(new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Map,
            SchemaVersion = "1.0",
            Format = "honua_map_package.v1",
            Body = document.RootElement.Clone(),
        });
    }
}
