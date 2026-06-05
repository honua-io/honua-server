// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Console.Publications;

/// <summary>
/// API integration tests for the authenticated content publication endpoints. Uses an
/// in-memory store override so the HTTP + service path is exercised without a migrated
/// Postgres schema (the Postgres store has dedicated integration coverage).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ContentPublicationEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ContentPublicationEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IContentPublicationStore>();
                services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    [Endpoint("GET /api/v1/console/publications/{publicationId}")]
    public async Task Publish_ThenGet_ReturnsDurableImmutableVersionAndActiveRoute()
    {
        var detail = await PublishAsync("Quarterly Map", ContentPublicationKind.Map, payload: "content-v1");

        detail.Route.RouteSlug.Should().Be("quarterly-map");
        detail.Route.ActiveRevision.Should().Be(1);
        detail.Versions.Should().ContainSingle();

        var get = await _client.GetAsync($"/api/v1/console/publications/{detail.Route.PublicationId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await DeserializeAsync(get, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        fetched!.Route.ActiveVersionId.Should().Be(detail.Route.ActiveVersionId);
        fetched.Versions[0].ContentHash.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_DuplicateSlug_ReturnsConflictWithoutLeakingInternals()
    {
        await PublishAsync("Conflict Map", ContentPublicationKind.Map);

        var body = SerializePublish(new PublishContentRequest { Kind = ContentPublicationKind.Dashboard, RouteSlug = "Conflict Map" });
        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("already claimed");
        AssertNoSensitiveLeak(payload);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_WithEmptyBody_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent("null"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_WithNumericUndefinedPolicyVisibility_ReturnsBadRequest()
    {
        const string body = """
            {
              "kind": "map",
              "routeSlug": "Bad Visibility",
              "policy": {
                "visibility": 999
              }
            }
            """;

        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("visibility");
        AssertNoSensitiveLeak(payload);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/publications/{publicationId}")]
    public async Task Get_WithMalformedPublicationId_ReturnsBadRequestWithoutLeakingInternals()
    {
        var response = await _client.GetAsync("/api/v1/console/publications/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("publicationId must be a valid GUID");
        AssertNoSensitiveLeak(payload);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/publications/{publicationId}/versions/{versionSelector}")]
    public async Task GetVersion_ByRevisionAndVersionId_ReturnsImmutableVersion_AndRejectsBadSelector()
    {
        var detail = await PublishAsync("Versioned Map", ContentPublicationKind.Map);
        var publicationId = detail.Route.PublicationId;
        var versionId = detail.Route.ActiveVersionId;

        var byRevision = await _client.GetAsync($"/api/v1/console/publications/{publicationId}/versions/1");
        byRevision.StatusCode.Should().Be(HttpStatusCode.OK);

        var byId = await _client.GetAsync($"/api/v1/console/publications/{publicationId}/versions/{versionId}");
        byId.StatusCode.Should().Be(HttpStatusCode.OK);

        var bad = await _client.GetAsync($"/api/v1/console/publications/{publicationId}/versions/not-a-version");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications/{publicationId}/republish")]
    [Endpoint("POST /api/v1/console/publications/{publicationId}/rollback")]
    public async Task RepublishThenRollback_MovesRoutePointerAndKeepsHistory()
    {
        var detail = await PublishAsync("Lifecycle Map", ContentPublicationKind.Map, payload: "v1");
        var publicationId = detail.Route.PublicationId;
        var v1Id = detail.Route.ActiveVersionId;

        var republishBody = JsonSerializer.Serialize(
            new RepublishContentRequest { ContentPayload = "v2" },
            ContentPublicationJsonContext.Default.RepublishContentRequest);
        var republish = await _client.PostAsync($"/api/v1/console/publications/{publicationId}/republish", JsonContent(republishBody));
        republish.StatusCode.Should().Be(HttpStatusCode.OK);
        var republished = await DeserializeAsync(republish, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        republished!.Route.ActiveRevision.Should().Be(2);

        var rollbackBody = JsonSerializer.Serialize(
            new RollbackContentRequest { TargetRevision = 1 },
            ContentPublicationJsonContext.Default.RollbackContentRequest);
        var rollback = await _client.PostAsync($"/api/v1/console/publications/{publicationId}/rollback", JsonContent(rollbackBody));
        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
        var rolledBack = await DeserializeAsync(rollback, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        rolledBack!.Route.ActiveRevision.Should().Be(1);
        rolledBack.Route.ActiveVersionId.Should().Be(v1Id);
        rolledBack.Route.RollbackTargetVersionId.Should().Be(v1Id);
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/console/publications/{publicationId}/policy")]
    public async Task UpdatePolicy_CreatesPublicLink_ReturnsTokenOnce()
    {
        var detail = await PublishAsync("Policy Map", ContentPublicationKind.Map);
        var publicationId = detail.Route.PublicationId;

        var body = JsonSerializer.Serialize(
            new UpdatePublicationPolicyRequest
            {
                Visibility = ContentPublicationVisibility.Public,
                CreatePublicLink = new ContentPublicLinkRequest { Label = "share", Token = "raw-secret-token" },
            },
            ContentPublicationJsonContext.Default.UpdatePublicationPolicyRequest);

        var response = await _client.PatchAsync($"/api/v1/console/publications/{publicationId}/policy", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await DeserializeAsync(response, ContentPublicationJsonContext.Default.ContentPublicationPolicyUpdateResponse);
        result!.CreatedPublicLinkToken.Should().Be("raw-secret-token");
        result.CreatedPublicLinkId.Should().NotBeNullOrEmpty();
        result.Route.Policy.Visibility.Should().Be(ContentPublicationVisibility.Public);
        // The stored link carries only a hash, never the raw token.
        var link = result.Route.Policy.PublicLink.Links.Should().ContainSingle().Subject;
        link.TokenHash.Should().NotBeNullOrEmpty();
        link.TokenHash.Should().NotContain("raw-secret-token");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    [Endpoint("PATCH /api/v1/console/publications/{publicationId}/policy")]
    public async Task UpdatePolicy_OnDefaultPolicyRoute_ChangesVisibilityAndEmbed_Returns200()
    {
        // Regression for #1239: a publication created with a minimal default policy
        // ({"visibility":"organization"}) leaves the nested policy members (embed/share/
        // service/publicLink) absent from the JSON body, so they deserialize to null. A
        // subsequent minimal policy PATCH that only touches visibility + embed must
        // succeed (200) rather than NRE while dereferencing those nested members.
        const string createBody = """
            {
              "kind": "report",
              "routeSlug": "rpt-smoke",
              "title": "Smoke",
              "contentPayload": "a",
              "policy": { "visibility": "organization" }
            }
            """;

        var createResponse = await _client.PostAsync("/api/v1/console/publications", JsonContent(createBody));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await DeserializeAsync(createResponse, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        created.Should().NotBeNull();
        var publicationId = created!.Route.PublicationId;

        const string patchBody = """
            {
              "visibility": "public",
              "embed": { "allowEmbedding": false }
            }
            """;

        var patchResponse = await _client.PatchAsync(
            $"/api/v1/console/publications/{publicationId}/policy",
            JsonContent(patchBody));

        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await DeserializeAsync(patchResponse, ContentPublicationJsonContext.Default.ContentPublicationPolicyUpdateResponse);
        result.Should().NotBeNull();
        result!.Route.RouteSlug.Should().Be("rpt-smoke");
        result.Route.Policy.Visibility.Should().Be(ContentPublicationVisibility.Public);
        result.Route.Policy.Embed.AllowEmbedding.Should().BeFalse();
        // No public link was created or revoked by this update.
        result.CreatedPublicLinkId.Should().BeNull();
        result.Route.Policy.PublicLink.Links.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/console/publications/{publicationId}/policy")]
    public async Task UpdatePolicy_WithNumericUndefinedVisibility_ReturnsBadRequest()
    {
        var detail = await PublishAsync("Patch Bad Visibility", ContentPublicationKind.Map);

        var response = await _client.PatchAsync(
            $"/api/v1/console/publications/{detail.Route.PublicationId}/policy",
            JsonContent("""{ "visibility": 999 }"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("visibility");
        AssertNoSensitiveLeak(payload);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_ReportWithUnresolvedPanelAlias_ReturnsFieldAddressableErrors()
    {
        // A report document whose only panel references a binding alias that is not declared, plus a chart
        // panel with no Vega-Lite spec, must be rejected with field-level errors[] addressed by JSON Pointer.
        const string payload = """
            {
              "format": "honua.report-document.v1",
              "title": "Bad Report",
              "bindings": [ { "alias": "incidents", "contentRef": "content:incidents" } ],
              "panels": [ { "title": "Chart", "kind": "chart", "bindingAlias": "missing", "chartSpec": null } ]
            }
            """;

        var body = SerializePublish(new PublishContentRequest
        {
            Kind = ContentPublicationKind.Report,
            RouteSlug = "bad-report",
            Title = "Bad Report",
            ContentPayload = payload,
        });

        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var responseBody = await response.Content.ReadAsStringAsync();
        AssertNoSensitiveLeak(responseBody);

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        root.TryGetProperty("errors", out var errors).Should().BeTrue("the rejection must carry the field-level errors[] extension");
        errors.ValueKind.Should().Be(JsonValueKind.Array);

        var items = errors.EnumerateArray().ToList();
        items.Should().NotBeEmpty();

        // Every item carries the shared FieldValidationError shape: code, severity, message, and an
        // addressable JSON Pointer path.
        foreach (var item in items)
        {
            item.GetProperty("code").GetString().Should().NotBeNullOrEmpty();
            item.GetProperty("severity").GetString().Should().NotBeNullOrEmpty();
            item.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
            item.GetProperty("path").GetString().Should().NotBeNullOrEmpty();
        }

        var paths = items.Select(i => i.GetProperty("path").GetString()).ToList();
        paths.Should().Contain("/panels/0/bindingAlias");
        paths.Should().Contain("/panels/0/chartSpec");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_ReportWithDuplicateBindingAlias_ReturnsDuplicateError()
    {
        const string payload = """
            {
              "format": "honua.report-document.v1",
              "title": "Dup Report",
              "bindings": [
                { "alias": "incidents", "contentRef": "content:a" },
                { "alias": "incidents", "contentRef": "content:b" }
              ],
              "panels": [
                {
                  "title": "Chart",
                  "kind": "chart",
                  "bindingAlias": "incidents",
                  "chartSpec": { "$schema": "https://vega.github.io/schema/vega-lite/v5.json", "mark": "bar" }
                }
              ]
            }
            """;

        var body = SerializePublish(new PublishContentRequest
        {
            Kind = ContentPublicationKind.Report,
            RouteSlug = "dup-report",
            Title = "Dup Report",
            ContentPayload = payload,
        });

        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString())
            .ToList();
        codes.Should().Contain("publication.binding.alias.duplicate");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_ValidReportDocument_Succeeds()
    {
        // A well-formed report document (declared binding, referencing chart panel, valid Vega-Lite) passes
        // body validation and publishes — proving the validator is not over-eager.
        const string payload = """
            {
              "format": "honua.report-document.v1",
              "title": "Good Report",
              "bindings": [ { "alias": "incidents", "contentRef": "content:incidents" } ],
              "panels": [
                {
                  "title": "Chart",
                  "kind": "chart",
                  "bindingAlias": "incidents",
                  "chartSpec": { "$schema": "https://vega.github.io/schema/vega-lite/v5.json", "mark": "bar" }
                }
              ]
            }
            """;

        var body = SerializePublish(new PublishContentRequest
        {
            Kind = ContentPublicationKind.Report,
            RouteSlug = "good-report",
            Title = "Good Report",
            ContentPayload = payload,
        });

        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_MapWithBindingsLikePayload_IsNotBodyValidated()
    {
        // Map/non-report-dashboard kinds carry opaque payloads; the body validator must not reject them even
        // if they happen to look like a bindings/panels document.
        const string payload = """
            { "bindings": [ { "alias": "", "contentRef": "" } ], "panels": [ { "kind": "chart", "bindingAlias": "x" } ] }
            """;

        var body = SerializePublish(new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "opaque-map",
            Title = "Opaque Map",
            ContentPayload = payload,
        });

        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_ReportWithWrongTypedBindingsAndPanels_IsRejected()
    {
        // A report document whose bindings/panels members are present but the wrong JSON type must be
        // rejected (not silently treated as opaque), since the document declares the bindings/panels graph.
        const string payload = """{ "format": "honua.report-document.v1", "bindings": {}, "panels": "nope" }""";

        var body = SerializePublish(new PublishContentRequest
        {
            Kind = ContentPublicationKind.Report,
            RouteSlug = "wrong-typed-report",
            Title = "Wrong Typed Report",
            ContentPayload = payload,
        });

        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString())
            .ToList();
        codes.Should().Contain("publication.bindings.invalid");
        codes.Should().Contain("publication.panels.invalid");
    }

    private async Task<ContentPublicationDetail> PublishAsync(string slug, ContentPublicationKind kind, string? payload = null)
    {
        var body = SerializePublish(new PublishContentRequest { Kind = kind, RouteSlug = slug, Title = slug, ContentPayload = payload });
        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var detail = await DeserializeAsync(response, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        detail.Should().NotBeNull();
        return detail!;
    }

    private static string SerializePublish(PublishContentRequest request)
        => JsonSerializer.Serialize(request, ContentPublicationJsonContext.Default.PublishContentRequest);

    private static StringContent JsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    private static void AssertNoSensitiveLeak(string payload)
    {
        payload.Should().NotContain("Npgsql");
        payload.Should().NotContain("System.");
        payload.Should().NotContain("ConnectionString");
        payload.Should().NotContain("content_publication_routes");
        payload.Should().NotContain(" at Honua.");
    }
}
