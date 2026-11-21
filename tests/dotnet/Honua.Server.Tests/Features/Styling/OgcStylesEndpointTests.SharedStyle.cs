// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Styling;

public sealed partial class OgcStylesEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task SharedStyle_ImpactMatchesEtag_AndStaleSaveConflicts()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);
        var review = await LoadReviewAsync(client, styleId);
        var nextTag = await SaveAndConfirmAsync(client, review);
        await RejectStaleSavesAsync(client, review, nextTag);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    public async Task SharedStyle_ImpactReview_RequiresStyleManagementRights()
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            // Displace the development-authentication bypass, which makes every caller an admin.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        var resource = fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId];

        using var anonymousClient = fixture.CreateClient();
        using var anonymous = await anonymousClient.GetAsync(
            $"/ogc/styles/{Uri.EscapeDataString(resource.Metadata.Name)}/impact");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.Content.ReadAsStringAsync()).Should().NotContain("consumers");
    }

    private async Task<StyleReview> LoadReviewAsync(HttpClient client, string styleId)
    {
        var path = $"/ogc/styles/{Uri.EscapeDataString(styleId)}";
        var tag = await ReadStylesheetTagAsync(client, path);
        var impactHash = await ReadImpactHashAsync(client, path, tag.ToString());
        var edited = JsonNode.Parse(BuildDefaultStyleJson())!;
        edited["name"] = "shared-style-save";
        return new StyleReview
        {
            Path = path,
            EntityTag = tag.ToString(),
            ImpactHash = impactHash,
            EditedBody = edited.ToJsonString()
        };
    }

    private async Task<EntityTagHeaderValue> ReadStylesheetTagAsync(HttpClient client, string path)
    {
        using var stylesheet = await client.GetAsync(path);
        stylesheet.Be200Ok();
        var tag = stylesheet.Headers.ETag;
        tag.Should().NotBeNull();
        tag!.IsWeak.Should().BeFalse();
        tag.ToString().Should().MatchRegex("^\"v\\d+-[0-9a-f]{64}\"$");

        using var sldRequest = new HttpRequestMessage(HttpMethod.Get, path);
        sldRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(Sld10MediaType));
        using var sld = await client.SendAsync(sldRequest);
        sld.Be200Ok();
        sld.Headers.ETag.Should().NotBeNull();
        sld.Headers.ETag!.ToString().Should().Be(tag.ToString());
        return tag;
    }

    private static async Task<string> ReadImpactHashAsync(HttpClient client, string path, string entityTag)
    {
        using var impactResponse = await client.GetAsync(path + "/impact");
        var impactBody = await impactResponse.Content.ReadAsStringAsync();
        impactResponse.StatusCode.Should().Be(HttpStatusCode.OK, impactBody);
        impactResponse.Headers.ETag.Should().NotBeNull();
        impactResponse.Headers.ETag!.ToString().Should().Be(entityTag);
        using var impact = JsonDocument.Parse(impactBody);
        var root = impact.RootElement;
        var styleId = Uri.UnescapeDataString(path["/ogc/styles/".Length..]);
        root.GetProperty("styleId").GetString().Should().Be(styleId);
        root.GetProperty("styleVersion").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        root.GetProperty("etag").GetString().Should().Be(entityTag);
        root.GetProperty("contentHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        root.GetProperty("impactHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        root.GetProperty("canEdit").GetBoolean().Should().BeTrue();
        root.GetProperty("impactComplete").GetBoolean().Should().BeTrue();
        root.GetProperty("associations").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("consumers").ValueKind.Should().Be(JsonValueKind.Array);
        return root.GetProperty("impactHash").GetString()!;
    }

    private static async Task<string> SaveAndConfirmAsync(HttpClient client, StyleReview review)
    {
        await PutAsync(client, review, new StylePut
        {
            Body = review.EditedBody,
            IfMatch = review.EntityTag,
            ImpactHash = review.ImpactHash,
            Expected = HttpStatusCode.NoContent
        });

        using var refreshed = await client.GetAsync(review.Path);
        refreshed.Be200Ok();
        var nextTag = refreshed.Headers.ETag!.ToString();
        nextTag.Should().NotBe(review.EntityTag);
        return nextTag;
    }

    private static async Task RejectStaleSavesAsync(HttpClient client, StyleReview review, string nextTag)
    {
        await PutAsync(client, review, new StylePut
        {
            Body = review.EditedBody,
            IfMatch = review.EntityTag,
            ImpactHash = review.ImpactHash,
            Expected = HttpStatusCode.PreconditionFailed
        });
        (await client.GetAsync(review.Path)).Headers.ETag!.ToString().Should().Be(nextTag);

        await PutAsync(client, review, new StylePut
        {
            Body = review.EditedBody,
            IfMatch = nextTag,
            ImpactHash = await OtherImpactHashAsync(client, review.Path),
            Expected = HttpStatusCode.PreconditionFailed
        });
        (await client.GetAsync(review.Path)).Headers.ETag!.ToString().Should().Be(nextTag);
        await PutAsync(client, review, new StylePut
        {
            Body = BuildDefaultStyleJson(),
            Expected = HttpStatusCode.NoContent
        });
    }

    private static async Task<string> OtherImpactHashAsync(HttpClient client, string path)
    {
        using var impact = await client.GetAsync(path + "/impact");
        using var document = JsonDocument.Parse(await impact.Content.ReadAsStringAsync());
        var currentHash = document.RootElement.GetProperty("impactHash").GetString()!;
        return new string(currentHash[0] == '0' ? '1' : '0', 64);
    }

    private static async Task PutAsync(HttpClient client, StyleReview review, StylePut save)
    {
        using var content = new StringContent(save.Body, Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, review.Path) { Content = content };
        if (save.IfMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", save.IfMatch);
        }

        if (save.ImpactHash is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Honua-Style-Impact", save.ImpactHash);
        }

        (await client.SendAsync(request)).StatusCode.Should().Be(save.Expected);
    }

    private sealed class StyleReview
    {
        public required string Path { get; init; }

        public required string EntityTag { get; init; }

        public required string ImpactHash { get; init; }

        public required string EditedBody { get; init; }
    }

    private sealed class StylePut
    {
        public required string Body { get; init; }

        public string? IfMatch { get; init; }

        public string? ImpactHash { get; init; }

        public required HttpStatusCode Expected { get; init; }
    }
}
