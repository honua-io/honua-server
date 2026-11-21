// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

public sealed partial class OgcStylesEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_ConcurrentLayerStyleSaves_WithOneEtag_StoreExactlyOne()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        await RaceTwoSavesAsync(client, await LoadReviewAsync(client, styleId));
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_ConcurrentCatalogStyleSaves_WithOneEtag_StoreExactlyOne()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateCatalogStyleAsync(client);

        await RaceTwoSavesAsync(client, await LoadReviewAsync(client, styleId));
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_ConcurrentMirroredStyleSaves_WithOneEtag_StoreExactlyOne()
    {
        var client = _fixture.CreateAdminClient();
        await SeedAndResolveStyleIdAsync(client);

        await RaceTwoSavesAsync(client, await LoadReviewAsync(client, $"style-layer-{WebAppFixture.TestLayerId}"));
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_SaveWaitingOnTheSaveLock_RechecksAfterTheConcurrentSave()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);
        await WithLongSaveLockWaitAsync(() => SaveWaitsThenRechecksAsync(client, styleId));
    }

    private async Task SaveWaitsThenRechecksAsync(HttpClient client, string styleId)
    {
        var review = await LoadReviewAsync(client, styleId);
        using var scope = _fixture.Services.CreateScope();
        var target = await scope.ServiceProvider.GetRequiredService<IOgcStyleProjection>().GetWriteTargetAsync(styleId);

        Task<HttpStatusCode> waiting;
        await using (var held = await scope.ServiceProvider.GetRequiredService<IStyleSaveLock>().TryAcquireAsync(target!.LockKey))
        {
            held.Should().NotBeNull();
            waiting = SendConditionalPutAsync(client, review, "waited");
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            waiting.IsCompleted.Should().BeFalse("a conditional save waits for the target's save lock");

            using var other = new StringContent(NamedStyleJson("landed-first"), Encoding.UTF8, MapboxStyleMediaType);
            (await client.PutAsync(review.Path, other)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await waiting).Should().Be(HttpStatusCode.PreconditionFailed);
        (await StoredStyleNameAsync(client, review.Path)).Should().Be("landed-first");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_SaveThatCannotTakeTheSaveLock_Returns409AndStoresNothing()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateCatalogStyleAsync(client);
        var review = await LoadReviewAsync(client, styleId);
        using var scope = _fixture.Services.CreateScope();
        var target = await scope.ServiceProvider.GetRequiredService<IOgcStyleProjection>().GetWriteTargetAsync(styleId);

        await using (var held = await scope.ServiceProvider.GetRequiredService<IStyleSaveLock>().TryAcquireAsync(target!.LockKey))
        {
            held.Should().NotBeNull();
            (await SendConditionalPutAsync(client, review, "blocked")).Should().Be(HttpStatusCode.Conflict);
        }

        (await StoredStyleNameAsync(client, review.Path)).Should().NotBe("blocked");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_SaveLandingAfterThePreconditionCheck_IsNotOverwritten()
    {
        var client = _fixture.CreateAdminClient();
        var layerStyleId = await SeedAndResolveStyleIdAsync(client);
        var catalogStyleId = await CreateCatalogStyleAsync(client);

        foreach (var styleId in new[] { layerStyleId, catalogStyleId })
        {
            await LateConditionalWriteLosesAsync(client, styleId);
        }
    }

    // The precondition read the version, another save landed, then the checked save reached the store.
    private async Task LateConditionalWriteLosesAsync(HttpClient client, string styleId)
    {
        using var scope = _fixture.Services.CreateScope();
        var projection = scope.ServiceProvider.GetRequiredService<IOgcStyleProjection>();
        var path = $"/ogc/styles/{Uri.EscapeDataString(styleId)}";
        var checkedVersion = (await projection.GetWriteTargetAsync(styleId))?.Version;
        checkedVersion.Should().NotBeNull();

        using (var interleaved = new StringContent(NamedStyleJson("interleaved"), Encoding.UTF8, MapboxStyleMediaType))
        {
            (await client.PutAsync(path, interleaved)).StatusCode.Should().Be(HttpStatusCode.NoContent, styleId);
        }

        var late = await projection.UpdateStyleAsync(styleId, NamedStyleJson("late"), new OgcStyleWriteOptions { ExpectedVersion = checkedVersion });

        late.Status.Should().Be(OgcStyleUpdateStatus.PreconditionFailed, styleId);
        (await StoredStyleNameAsync(client, path)).Should().Be("interleaved", styleId);

        var current = (await projection.GetWriteTargetAsync(styleId))?.Version;
        var retried = await projection.UpdateStyleAsync(styleId, NamedStyleJson("retried"), new OgcStyleWriteOptions { ExpectedVersion = current });
        retried.Status.Should().Be(OgcStyleUpdateStatus.Updated, styleId);
        (await StoredStyleNameAsync(client, path)).Should().Be("retried", styleId);
    }

    private Task RaceTwoSavesAsync(HttpClient client, StyleReview review) =>
        WithLongSaveLockWaitAsync(() => RaceAsync(client, review));

    // The default two-second wait could turn a slow first save on a loaded host into a 409 for the second.
    private async Task WithLongSaveLockWaitAsync(Func<Task> body)
    {
        const string key = "Styles:SaveLockWaitMilliseconds";
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        var previous = configuration[key];
        configuration[key] = "30000";
        try
        {
            await body();
        }
        finally
        {
            configuration[key] = previous;
        }
    }

    private static async Task RaceAsync(HttpClient client, StyleReview review)
    {
        var names = new[] { "race-a", "race-b" };
        var statuses = await Task.WhenAll(names.Select(name => SendConditionalPutAsync(client, review, name)));

        statuses.Should().BeEquivalentTo([HttpStatusCode.NoContent, HttpStatusCode.PreconditionFailed]);
        var winner = names[Array.IndexOf(statuses, HttpStatusCode.NoContent)];
        (await StoredStyleNameAsync(client, review.Path)).Should().Be(winner);
    }

    private static async Task<HttpStatusCode> SendConditionalPutAsync(HttpClient client, StyleReview review, string name)
    {
        using var content = new StringContent(NamedStyleJson(name), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, review.Path) { Content = content };
        request.Headers.TryAddWithoutValidation("If-Match", review.EntityTag);
        request.Headers.TryAddWithoutValidation("X-Honua-Style-Impact", review.ImpactHash);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> CreateCatalogStyleAsync(HttpClient client)
    {
        var styleId = $"atomic-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return styleId;
    }

    private static async Task<string?> StoredStyleNameAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
    }

    private static string NamedStyleJson(string name)
    {
        var style = JsonNode.Parse(BuildDefaultStyleJson())!;
        style["name"] = name;
        return style.ToJsonString();
    }
}
