// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Admin;

public partial class SecureConnectionEndpointsTests
{
    // The seeded test schema has no services foreign key, so the registry stands in for the
    // migrated database (DatabaseMigrationTests proves migration 008 raises this refusal).
    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/connections/{id}")]
    public async Task DeleteConnection_ReferencedByService_ReturnsConflict()
    {
        var connectionId = Guid.NewGuid();
        var registry = Substitute.For<ISecureConnectionRegistry>();
        registry.DeleteConnectionAsync(connectionId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PostgresException(
                "update or delete on table \"data_connections\" violates RESTRICT setting of foreign key constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.RestrictViolation));
        var localFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<ISecureConnectionRegistry>();
                services.AddSingleton(registry);
            });
        try
        {
            await localFixture.InitializeAsync();
            using var client = localFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            using var response = await client.DeleteAsync($"/api/v1/admin/connections/{connectionId}");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("in use", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await localFixture.DisposeAsync();
        }
    }
}
