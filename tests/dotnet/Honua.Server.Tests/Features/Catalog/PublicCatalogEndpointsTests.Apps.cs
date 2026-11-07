// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class PublicCatalogEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_PublishedApp_UsesImmutableDisplayAndBidirectionalLineage()
    {
        var (draft, version) = await CreateAppAsync("public");
        using var before = await ReadAsync(_anonymous, "?type=app");
        Assert.Empty(before.RootElement.GetProperty("items").EnumerateArray());
        await PublishAppAsync(version, "public");
        using var published = await ReadAsync(_anonymous, "?type=app");
        var app = Assert.Single(published.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(version.ItemId.ToString("D"), app.GetProperty("id").GetString());
        Assert.Equal("Survey app", app.GetProperty("title").GetString());
        Assert.Equal("/images/survey.png", app.GetProperty("thumbnail").GetString());
        Assert.EndsWith(StudioPublishedRoutes.BuildActiveUrl("/apps/" + version.PackageKey), app.GetProperty("url").GetString());
        Assert.Equal("service", Assert.Single(app.GetProperty("services").EnumerateArray()).GetString());
        await Store.UpdateDraftAsync(draft with { Envelope = draft.Envelope with { Display = new() { Title = "Draft title" } } });
        using var unchanged = await ReadAsync(_anonymous, "?type=app");
        Assert.Equal("Survey app", unchanged.RootElement.GetProperty("items")[0].GetProperty("title").GetString());
        using var services = await ReadAsync(_anonymous, "?type=service");
        var service = Assert.Single(services.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(version.ItemId.ToString("D"), Assert.Single(service.GetProperty("apps").EnumerateArray()).GetString());
        await Assert.IsAssignableFrom<IStudioPackageUnpublisher>(Store).UnpublishAsync(new()
        {
            RequestId = Guid.NewGuid(), ItemId = version.ItemId, ExpectedPublishedVersionId = version.VersionId, ActorId = "owner"
        });
        using var withdrawn = await ReadAsync(_anonymous, "?type=app");
        Assert.Equal(0, withdrawn.RootElement.GetProperty("total").GetInt32());
        using var detached = await ReadAsync(_anonymous, "?type=service");
        Assert.Empty(detached.RootElement.GetProperty("items")[0].GetProperty("apps").EnumerateArray());
    }

    [IntegrationTheory]
    [InlineData("public", 1, 1)]
    [InlineData("organization", 0, 1)]
    [InlineData("personal", 0, 0)]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_AppVisibility_FiltersBeforeTotalsAndFacets(string visibility, int anonymousCount, int memberCount)
    {
        var (_, version) = await CreateAppAsync(visibility);
        await PublishAppAsync(version, visibility);
        foreach (var (client, count) in new[] { (_anonymous, anonymousCount), (_member, memberCount) })
        {
            using var result = await ReadAsync(client, "?type=app");
            Assert.Equal(count, result.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(count, result.RootElement.GetProperty("items").GetArrayLength());
            if (count == 0)
            {
                Assert.All(result.RootElement.GetProperty("facets").EnumerateObject(), facet => Assert.Empty(facet.Value.EnumerateArray()));
                Assert.DoesNotContain(version.ItemId.ToString("D"), result.RootElement.GetRawText(), StringComparison.Ordinal);
            }
        }
    }

    private IStudioPackageStore Store => _fixture.Services.GetRequiredService<IStudioPackageStore>();

    private async Task<(StudioPackageDraft Draft, StudioContentVersion Version)> CreateAppAsync(string visibility)
    {
        var draft = await Store.CreateDraftAsync(new()
        {
            DraftId = Guid.NewGuid(), ItemId = Guid.NewGuid(), PackageKey = "survey-" + Guid.NewGuid().ToString("N"),
            Family = StudioPackageFamily.App, OwnerId = "app-owner", Generation = 1,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Envelope = new()
            {
                Family = StudioPackageFamily.App, SchemaVersion = "1.0",
                Display = new() { Title = "Survey app", Description = "Published survey", Thumbnail = "/images/survey.png" },
                Dependencies = [new() { Kind = "service", Ref = "service" }],
                PublicationIntent = new() { Visibility = visibility }
            }
        });
        return (draft, await Store.CreateVersionAsync(draft, null, "app-owner"));
    }

    private Task<StudioPublicationRequest> PublishAppAsync(StudioContentVersion version, string visibility)
        => Store.CreatePublicationRequestAsync(new()
        {
            RequestId = Guid.NewGuid(), ItemId = version.ItemId, VersionId = version.VersionId,
            Status = StudioPublicationRequestStatus.Accepted, RequestedBy = "app-owner", CreatedAt = DateTimeOffset.UtcNow,
            Intent = new() { Route = "/apps/" + version.PackageKey, Visibility = visibility }
        }, version.VersionId);
}
