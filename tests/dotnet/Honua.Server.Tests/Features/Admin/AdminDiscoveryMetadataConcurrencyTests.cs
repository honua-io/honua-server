// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class AdminDiscoveryMetadataEndpointsConcurrencyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
        .ConfigureServices(services => services.AddSingleton<IMetadataV2GraphStore>(provider =>
            new ConflictingGraphStore(provider.GetRequiredService<TestMetadataV2GraphProvider>())));

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/discovery")]
    [InlineData("/api/v1/admin/metadata/layers/0/discovery")]
    [InlineData("/api/v1/admin/services/test/discovery")]
    public async Task UpdateDiscovery_ConcurrentCommit_ReturnsConflictWithoutOverwriting(string path)
    {
        var before = _fixture.GetCurrentV2GraphSnapshot();
        using var content = JsonContent.Create(new DiscoveryMetadataUpdateRequest
        {
            Etag = before.Etag,
            Title = "Rejected title"
        }, DiscoveryMetadataJsonContext.Default.DiscoveryMetadataUpdateRequest);
        var response = await _fixture.CreateAdminClient().PutAsync(path, content);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeSameAs(before.Graph);
    }

    private sealed class ConflictingGraphStore(TestMetadataV2GraphProvider provider) : IMetadataV2GraphStore
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            provider.GetCurrentAsync(cancellationToken);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default) =>
            provider.GetByRevisionAsync(revision, cancellationToken);

        public Task<MetadataV2GraphSnapshot> SaveAsync(MetadataV2Graph graph, string? expectedEtag,
            CancellationToken cancellationToken = default) => throw new MetadataV2GraphConcurrencyException(
                "Concurrent graph commit.", expectedEtag, "concurrent-etag");

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(long revision, string? expectedCurrentEtag,
            CancellationToken cancellationToken = default) =>
            provider.ActivateRevisionAsync(revision, expectedCurrentEtag, cancellationToken);
    }
}
