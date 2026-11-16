// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ServiceSettingsEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetSettings_ReturnsMetadataEtagForAccessPolicyPrecondition()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.ToString().Should().Be(_fixture.GetCurrentV2GraphSnapshot().Etag);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_WithStaleMetadataEtag_RejectsWithoutChangingGraph()
    {
        var inspected = _fixture.GetCurrentV2GraphSnapshot();
        var provider = _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>();
        provider.SetGraph(inspected.Graph with { Revision = inspected.Graph.Revision + 1 },
            schema: _fixture.MetadataGraphSchema);
        var before = _fixture.GetCurrentV2GraphSnapshot();

        var response = await _client.PutAsJsonAsync("/api/v1/admin/services/test/access-policy",
            new { expectedMetadataEtag = inspected.Etag, allowAnonymous = false });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _fixture.GetCurrentV2GraphSnapshot().Should().BeEquivalentTo(before);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_WithMatchingMetadataEtag_UpdatesPolicy()
    {
        var inspected = _fixture.GetCurrentV2GraphSnapshot();
        var response = await _client.PutAsJsonAsync("/api/v1/admin/services/test/access-policy",
            new { expectedMetadataEtag = inspected.Etag, allowAnonymous = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _fixture.GetCurrentV2GraphSnapshot().Graph.Services
            .Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.AccessPolicy != null && !service.AccessPolicy.AllowAnonymous);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_WithEmptyMetadataEtag_RejectsWithoutChangingGraph()
    {
        var before = _fixture.GetCurrentV2GraphSnapshot();
        var response = await _client.PutAsJsonAsync("/api/v1/admin/services/test/access-policy",
            new { expectedMetadataEtag = "", allowAnonymous = false });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _fixture.GetCurrentV2GraphSnapshot().Should().BeEquivalentTo(before);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_ConcurrentSaveConflict_RejectsPinnedWrite()
    {
        await AssertConcurrentSaveConflictAsync(_fixture.GetCurrentV2GraphSnapshot().Etag);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_ConcurrentSaveConflict_RetriesUnpinnedWrite()
    {
        await AssertConcurrentSaveConflictAsync(null);
    }

    private async Task AssertConcurrentSaveConflictAsync(string? expectedEtag)
    {
        var before = _fixture.GetCurrentV2GraphSnapshot();
        var current = before;
        var store = Substitute.For<IMetadataV2GraphStore>();
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        store.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<MetadataV2GraphSnapshot>(current));
#pragma warning restore CA2012
        var saves = 0;
        store.SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saves++;
                if (saves == 1)
                {
                    current = new MetadataV2GraphSnapshot(before.Graph with { Revision = before.Graph.Revision + 1 }, "concurrent", DateTimeOffset.UtcNow);
                    throw new MetadataV2GraphConcurrencyException("Concurrent metadata writer", before.Etag, current.Etag);
                }
                current = new MetadataV2GraphSnapshot(call.ArgAt<MetadataV2Graph>(0), "saved", DateTimeOffset.UtcNow);
                return Task.FromResult(current);
            });
        await using var app = CreateConcurrentAccessApp(store);
        await app.InitializeAsync();
        using var client = app.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        var response = await client.PutAsJsonAsync("/api/v1/admin/services/test/access-policy",
            new { expectedMetadataEtag = expectedEtag, allowAnonymous = false });

        response.StatusCode.Should().Be(expectedEtag is not null ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        saves.Should().Be(expectedEtag is not null ? 1 : 2);
        if (expectedEtag is not null)
        {
            current.Graph.Should().BeEquivalentTo(before.Graph with { Revision = before.Graph.Revision + 1 });
        }
    }

    private static WebAppFixture CreateConcurrentAccessApp(IMetadataV2GraphStore store) =>
        new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(store);
            });
}
