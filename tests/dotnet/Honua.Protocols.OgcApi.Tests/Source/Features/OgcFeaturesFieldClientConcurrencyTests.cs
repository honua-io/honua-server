// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.Protocols.Ogc.Api.Features;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesFieldClientConcurrencyTests : IAsyncLifetime
{
    private readonly IFeatureChangeEventPublisher _publisher = Substitute.For<IFeatureChangeEventPublisher>();
    private readonly WebAppFixture _fixture;

    public OgcFeaturesFieldClientConcurrencyTests()
    {
        _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ReplaceService(_publisher);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [Operation(Operations.Update)]
    [Operation(Operations.Delete)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task MutateFeature_OfflineClientUsesStaleVersion_PreservesOnlineEdit(string method)
    {
        var id = await _fixture.InsertFeatureAsync(0, "Original field observation");
        var itemPath = $"/ogc/features/collections/0/items/{id}";
        using var original = await _fixture.Client.GetAsync(itemPath);
        original.StatusCode.Should().Be(HttpStatusCode.OK);
        original.Headers.ETag.Should().NotBeNull();
        var staleTag = original.Headers.ETag!.ToString();

        using var onlineEdit = BuildMutation("PATCH", itemPath, "Online observation");
        onlineEdit.Headers.TryAddWithoutValidation("If-Match", staleTag);
        using var onlineResult = await _fixture.Client.SendAsync(onlineEdit);
        onlineResult.StatusCode.Should().Be(HttpStatusCode.OK);

        using var winner = await _fixture.Client.GetAsync(itemPath);
        winner.StatusCode.Should().Be(HttpStatusCode.OK);
        winner.Headers.ETag.Should().NotBeNull();
        winner.Headers.ETag!.ToString().Should().NotBe(staleTag);
        var winningBody = await winner.Content.ReadAsStringAsync();

        using var offlineEdit = BuildMutation(method, itemPath, "Offline observation");
        offlineEdit.Headers.TryAddWithoutValidation("If-Match", staleTag);
        using var rejected = await _fixture.Client.SendAsync(offlineEdit);
        rejected.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        await AssertCurrentVersionAsync(itemPath, winner.Headers.ETag!, winningBody);
        var change = PublishedChanges().Should().ContainSingle().Subject;
        change.ObjectId.Should().Be(id);
        change.Operation.Should().Be("update");
        change.LayerId.Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create, Operations.Update, Operations.Delete)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task MutateFeature_CreateUpdateDelete_PublishesCommittedChanges()
    {
        using var content = new StringContent(
            """{"type":"Feature","geometry":null,"properties":{"name":"Field observation"}}""",
            Encoding.UTF8, "application/geo+json");
        using var created = await _fixture.Client.PostAsync("/ogc/features/collections/0/items", content);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var feature = JsonSerializer.Deserialize(await created.Content.ReadAsStringAsync(), OgcJsonContext.Default.GeoJsonFeature)!;
        var path = $"/ogc/features/collections/0/items/{feature.Id}";
        using var patch = BuildMutation("PATCH", path, "Updated observation");
        using var updated = await _fixture.Client.SendAsync(patch);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var current = await _fixture.Client.GetAsync(path);
        (await current.Content.ReadAsStringAsync()).Should().Contain("Updated observation");
        using var deleted = await _fixture.Client.DeleteAsync(path);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var missing = await _fixture.Client.GetAsync(path);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var changes = PublishedChanges();
        changes.Select(change => change.Operation).Should().Equal("create", "update", "delete");
        changes.Should().OnlyContain(change => change.ObjectId == Convert.ToInt64(feature.Id) && change.LayerId == 0);
        changes.Should().OnlyContain(change => !string.IsNullOrWhiteSpace(change.RequestId));
    }

    private async Task AssertCurrentVersionAsync(string itemPath, System.Net.Http.Headers.EntityTagHeaderValue tag, string winningBody)
    {
        using var current = await _fixture.Client.GetAsync(itemPath);
        current.StatusCode.Should().Be(HttpStatusCode.OK);
        current.Headers.ETag.Should().Be(tag);
        var currentBody = await current.Content.ReadAsStringAsync();
        var feature = JsonSerializer.Deserialize(currentBody, OgcJsonContext.Default.GeoJsonFeature);
        feature!.Properties["name"]!.ToString().Should().Be("Online observation");
        currentBody.Should().Be(winningBody);
    }

    private FeatureChangeEventRequest[] PublishedChanges() => _publisher.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IFeatureChangeEventPublisher.PublishAsync))
        .Select(call => (FeatureChangeEventRequest)call.GetArguments()[0]!).ToArray();

    private static HttpRequestMessage BuildMutation(string method, string itemPath, string name)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), itemPath);
        if (method == "PATCH")
        {
            request.Content = new StringContent(
                $$$"""{"properties":{"name":"{{{name}}}"}}""",
                Encoding.UTF8, "application/merge-patch+json");
        }
        else if (method == "PUT")
        {
            request.Content = new StringContent(
                $$$"""{"type":"Feature","geometry":null,"properties":{"name":"{{{name}}}"}}""",
                Encoding.UTF8, "application/geo+json");
        }

        return request;
    }
}
