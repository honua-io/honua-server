// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

// Contingent values and plugin edit hooks hold on OGC API Features item edits end to end.
[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesEditGateScenarioTests : IAsyncLifetime
{
    private const string Items = "/ogc/features/collections/0/items";
    private readonly BlockingHooks _hooks = new();
    private readonly WebAppFixture _fixture;

    public OgcFeaturesEditGateScenarioTests()
    {
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Community)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFeatureEditHooks>();
                services.AddSingleton<IFeatureEditHooks>(_hooks);
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create, Operations.Update)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task ContingentValues_RejectInvalidCreateReplaceAndMergedPatch()
    {
        await _fixture.UpdateV2ResourceContingentValueGroupsAsync(0, [ParkGroup()]);
        var created = await SendAsync(HttpMethod.Post, Items, FeatureJson("valid", "green"));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = await ReadIdAsync(created);

        var invalidCreate = await SendAsync(HttpMethod.Post, Items, FeatureJson("invalid", "red"));
        var invalidReplace = await SendAsync(HttpMethod.Put, $"{Items}/{id}", FeatureJson("replaced", "red"));
        var invalidPatch = await SendAsync(HttpMethod.Patch, $"{Items}/{id}", """{"properties":{"description":"red"}}""");
        var validPatch = await SendAsync(HttpMethod.Patch, $"{Items}/{id}", """{"properties":{"name":"renamed"}}""");

        foreach (var rejected in new[] { invalidCreate, invalidReplace, invalidPatch })
        {
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await rejected.Content.ReadAsStringAsync()).Should().Contain("parkColour");
        }

        validPatch.StatusCode.Should().Be(HttpStatusCode.OK, "the merged row keeps the valid category and description");
        var stored = await ReadPropertiesAsync(id);
        stored.GetProperty("description").GetString().Should().Be("green");
        stored.GetProperty("name").GetString().Should().Be("renamed");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task PluginHooks_RejectBeforeTheWrite_AndSeeOnlyCommittedRowsAfter()
    {
        var blocked = await SendAsync(HttpMethod.Post, Items, FeatureJson(BlockingHooks.BlockedName, "green"));
        var allowed = await SendAsync(HttpMethod.Post, Items, FeatureJson("allowed", "green"));

        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("rejected by plugin");
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await ReadIdAsync(allowed);
        _hooks.CommittedCreates.Should().ContainSingle("only the committed create reaches the after-hooks")
            .Which.Should().Be(id);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                method == HttpMethod.Patch ? "application/merge-patch+json" : "application/geo+json"),
        };
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<JsonElement> ReadPropertiesAsync(long id)
    {
        var response = await _fixture.Client.GetAsync($"{Items}/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("properties").Clone();
    }

    private static async Task<long> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id");
        return id.ValueKind == JsonValueKind.Number
            ? id.GetInt64()
            : long.Parse(id.GetString()!, CultureInfo.InvariantCulture);
    }

    private static string FeatureJson(string name, string description)
        => $$$"""
            {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.41,37.77]},
             "properties":{"name":"{{{name}}}","category":"park","description":"{{{description}}}"}}
            """;

    private static MetadataV2ContingentValueGroup ParkGroup() => new()
    {
        Name = "parkColour",
        Restrictive = true,
        Fields = ["category", "description"],
        ContingentValues =
        [
            new MetadataV2ContingentValue
            {
                Id = 1,
                Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["category"] = new() { Type = "code", Code = JsonElement.Parse("\"park\"") },
                    ["description"] = new() { Type = "code", Code = JsonElement.Parse("\"green\"") },
                },
            },
        ],
    };

    private sealed class BlockingHooks : IFeatureEditHooks
    {
        public const string BlockedName = "blocked";

        public List<long> CommittedCreates { get; } = [];

        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<FeatureEditHookRejection> rejections =
            [
                .. FeatureEditRows.Enumerate(batch)
                    .Where(static row => IsBlocked(row.Feature))
                    .Select(static row => new FeatureEditHookRejection(row.Kind, row.Index, "rejected by plugin")),
            ];
            return Task.FromResult(rejections);
        }

        public Task RunAfterAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            FeatureEditResult result,
            CancellationToken cancellationToken)
        {
            foreach (var row in FeatureEditRows.Enumerate(batch).Where(candidate => FeatureEditRows.IsCommitted(result, candidate)))
            {
                if (row.Kind == FeatureEditOperationKind.Create && FeatureEditRows.ResultOf(result, row)?.ObjectId is { } id)
                {
                    CommittedCreates.Add(id);
                }
            }

            return Task.CompletedTask;
        }

        private static bool IsBlocked(Feature? feature)
            => feature is { } row
               && row.Attributes.TryGetValue("name", out var name)
               && string.Equals(Convert.ToString(name, CultureInfo.InvariantCulture), BlockedName, StringComparison.Ordinal);
    }
}
