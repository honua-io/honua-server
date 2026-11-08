// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public partial class SecureConnectionEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_StoreProfileRefusal_ReturnsBadRequestWithFullVariant()
    {
        var registry = Substitute.For<ISecureConnectionRegistry>();
        registry.CreateConnectionAsync(Arg.Any<DataConnection>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<DataConnection>(new DataStoreProviderUnavailableException()));
        var request = new CreateSecureConnectionRequest
        {
            Name = "profile-fixture",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "fixture",
            Username = "fixture",
            Password = "fixture-password",
            SslRequired = true,
            SslMode = "Require"
        };

        await WithRegistryFixtureAsync(registry, async client =>
        {
            using var response = await client.PostAsJsonAsync("/api/v1/admin/connections", request, _jsonOptions);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("-full");
        });
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    public async Task UpdateConnection_StoreProfileRefusal_ReturnsBadRequestWithFullVariant()
    {
        var registry = Substitute.For<ISecureConnectionRegistry>();
        var connection = new DataConnection
        {
            ConnectionId = Guid.NewGuid(),
            Name = "profile-fixture",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "fixture",
            Username = "fixture",
            EncryptedConnectionString = [1, 2, 3],
            EncryptionKeyVersion = 1,
            SslMode = SslMode.Require
        };
        registry.GetConnectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(connection);
        registry.UpdateConnectionAsync(Arg.Any<DataConnection>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<DataConnection>(new DataStoreProviderUnavailableException()));

        await WithRegistryFixtureAsync(registry, async client =>
        {
            using var response = await client.PutAsJsonAsync($"/api/v1/admin/connections/{connection.ConnectionId}",
                new UpdateSecureConnectionRequest { Description = "fixture" }, _jsonOptions);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("-full");
        });
    }

    private static async Task WithRegistryFixtureAsync(ISecureConnectionRegistry registry, Func<HttpClient, Task> proof)
    {
        var fixture = CreateFixture().ConfigureServices(services =>
        {
            services.RemoveAll<ISecureConnectionRegistry>();
            services.AddSingleton(registry);
        });
        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            await proof(client);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
