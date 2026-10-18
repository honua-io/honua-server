// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.ContractTesting)]
public sealed class LayerSourceBindingSecurityTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingExternalConnection_DoesNotReadCollidingManagedTable(bool metadataPresent)
    {
        const string sentinel = "gp-managed-connection-sentinel";
        await SeedAsync(sentinel);
        var source = Source();
        var request = new DagSourceRequest { LayerId = 0, Where = $"name = '{sentinel}'" };
        (await CollectAsync(source, request)).Should().ContainSingle();

        var original = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var missingConnectionId = Guid.NewGuid().ToString();
        var binding = original.Index.StorageBindingsByStorageLayerId[0];
        var connections = original.Graph.Connections.ToList();
        if (metadataPresent)
        {
            connections.Add(new MetadataV2Connection
            {
                Metadata = new MetadataV2ObjectMetadata { Id = missingConnectionId, Name = "missing-external" },
                Provider = DataProviderNames.Postgis
            });
        }
        var unavailableSource = new TestMetadataV2GraphProvider(original.Graph with
        {
            Connections = connections,
            StorageBindings = original.Graph.StorageBindings.Select(candidate => candidate.Metadata.Id == binding.Metadata.Id
                ? candidate with { ConnectionId = missingConnectionId } : candidate).ToArray()
        });
        source = Source(unavailableSource);

        // Same storage id, table name and query as the readable managed sentinel above.
        // Neither missing metadata nor missing secure credentials may redirect to that table.
        var act = () => CollectAsync(source, request);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connection or feature provider is unavailable*");
    }

    [IntegrationTest]
    public async Task ActiveJobSubmitter_ConstrainsBoundReadsDespiteAmbientAdministrator()
    {
        const string allowed = "gp-submitter-visible";
        const string denied = "gp-submitter-hidden";
        await SeedAsync(allowed);
        await SeedAsync(denied);
        var snapshot = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var resource = snapshot.Index.ResourcesByStorageLayerId[0];
        await _fixture.GetService<IRlsPolicyStore>().CreatePolicyAsync(new RlsPolicy
        {
            Service = "*", Layer = resource.Metadata.Name, Role = "gp-limited",
            Attribute = "name", ClaimType = "gp_allowed_name"
        });
        await _fixture.GetService<IFieldMaskPolicyStore>().CreatePolicyAsync(new FieldMaskPolicy
        {
            Service = "*", Layer = resource.Metadata.Name, Role = "gp-limited", Attribute = "description"
        });
        var accessor = _fixture.GetService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Test")),
            RequestServices = _fixture.GetService<IServiceProvider>()
        };
        try
        {
            var source = Source();
            var request = new DagSourceRequest { LayerId = 0, Where = $"name IN ('{allowed}', '{denied}')" };
            var admin = await CollectAsync(source, request);
            admin.Should().HaveCount(2);
            admin.Should().OnlyContain(feature => feature.Attributes.ContainsKey("description"));

            using (JobSecurityScope.Begin(new JobSecurityContext("submitter", null,
                [new(ClaimTypes.Role, "gp-limited"), new("gp_allowed_name", allowed)])))
            {
                var restricted = await CollectAsync(source, request);
                restricted.Should().ContainSingle().Which.Attributes["name"].Should().Be(allowed);
                restricted[0].Attributes.Should().NotContainKey("description");
            }
            (await CollectAsync(source, request)).Should().HaveCount(2,
                "leaving the job scope restores the ordinary request identity");
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private HonuaLayerDagSource Source(IMetadataV2GraphProvider? metadata = null) => new(
        _fixture.GetService<IStreamingFeatureStore>(), metadata ?? _fixture.GetService<IMetadataV2GraphProvider>(),
        queryRouter: _fixture.GetService<FeatureProviderQueryRouter>());

    private async Task SeedAsync(string name)
    {
        await _fixture.GetService<IFeatureWriter>().CreateAsync(0, Feature.Create(0, null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name).Add("description", "restricted-value")));
    }

    private static async Task<List<DagSourceFeature>> CollectAsync(HonuaLayerDagSource source, DagSourceRequest request)
    {
        List<DagSourceFeature> result = [];
        await foreach (var feature in source.ReadAsync(request)) result.Add(feature);
        return result;
    }
}
