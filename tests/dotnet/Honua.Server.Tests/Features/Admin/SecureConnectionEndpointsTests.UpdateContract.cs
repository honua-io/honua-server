// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Admin;

public partial class SecureConnectionEndpointsTests
{
    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    public async Task UpdateConnection_DeletedBeforeWrite_ReturnsNotFound()
    {
        var connection = SecretReferenceConnection($"race-{Guid.NewGuid():N}");
        var registry = Substitute.For<ISecureConnectionRegistry>();
        registry.GetConnectionAsync(connection.ConnectionId, Arg.Any<CancellationToken>()).Returns(connection);
        registry.UpdateConnectionAsync(Arg.Any<DataConnection>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ResourceNotFoundException($"Connection with ID {connection.ConnectionId} not found for update"));
        var localFixture = CreateFixture().ConfigureServices(services =>
        {
            services.RemoveAll<ISecureConnectionRegistry>();
            services.AddSingleton(registry);
        });
        try
        {
            await localFixture.InitializeAsync();
            using var client = localFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            using var response = await client.PutAsJsonAsync(
                $"/api/v1/admin/connections/{connection.ConnectionId}", new { description = "after delete" });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await localFixture.DisposeAsync();
        }
    }

    [Theory]
    [Trait("Tier", "Integration")]
    [Trait("Category", "Integration")]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    [InlineData("name", "renamed")]
    [InlineData("bucket", "other-bucket")]
    [InlineData("prefix", "other/")]
    [InlineData("prefix", "")]
    [InlineData("roleArn", " ")]
    [InlineData("region", "us-west-2")]
    [InlineData("roleArn", "arn:aws:iam::123456789012:role/other")]
    [InlineData("externalId", "other-external-id")]
    public async Task UpdateDatabaseConnection_UnchangeableField_ReturnsBadRequest(string field, string value)
    {
        await InitializeDefaultApplicationAsync();
        var created = await _registry.CreateConnectionAsync(SecretReferenceConnection($"immutable-{Guid.NewGuid():N}"));

        using var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/connections/{created.ConnectionId}", new Dictionary<string, string> { [field] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"{field} cannot change on a database connection", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var stored = await _registry.GetConnectionAsync(created.ConnectionId);
        Assert.Equal(created.Name, stored!.Name);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    public async Task UpdateDatabaseConnection_UnchangedName_Succeeds()
    {
        await InitializeDefaultApplicationAsync();
        var created = await _registry.CreateConnectionAsync(SecretReferenceConnection($"same-name-{Guid.NewGuid():N}"));

        using var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/connections/{created.ConnectionId}", new { name = created.Name, description = "kept name" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _registry.GetConnectionAsync(created.ConnectionId);
        Assert.Equal("kept name", stored!.Description);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_ConcurrentSameName_OneCreatedOthersBadRequest()
    {
        await InitializeDefaultApplicationAsync();
        var request = new CreateSecureConnectionRequest
        {
            Name = $"concurrent-{Guid.NewGuid():N}",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            SecretReference = "env:HONUA_TEST_DB_CONNECTION",
            SecretType = "environment",
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(
            _ => _client.PostAsJsonAsync("/api/v1/admin/connections", request, _jsonOptions)));

        var statuses = responses.Select(response => response.StatusCode).ToList();
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(7, statuses.Count(status => status == HttpStatusCode.BadRequest));
        var stored = await _registry.GetConnectionByNameAsync(request.Name);
        Assert.NotNull(stored);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static DataConnection SecretReferenceConnection(string name) => DataConnection.CreateWithSecretReference(
        name: name,
        host: "localhost",
        port: 5432,
        databaseName: "testdb",
        username: "testuser",
        secretRef: "env:HONUA_TEST_DB_CONNECTION",
        secretType: "environment",
        createdBy: "test-user");
}
