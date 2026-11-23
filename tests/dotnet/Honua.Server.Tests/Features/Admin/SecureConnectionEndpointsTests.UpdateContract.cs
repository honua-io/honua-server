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
