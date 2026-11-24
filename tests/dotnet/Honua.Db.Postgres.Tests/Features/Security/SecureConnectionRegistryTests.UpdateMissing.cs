// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Security;

public partial class SecureConnectionRegistryTests
{
    // The admin PUT maps this to 404 when a DELETE lands between its read and its write.
    [IntegrationTest]
    public async Task UpdateConnectionAsync_DeletedConnection_ThrowsResourceNotFound()
    {
        var connection = DataConnection.CreateWithSecretReference(
            name: $"test-deleted-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            secretRef: "env:TEST_DB_CONNECTION",
            secretType: "environment",
            createdBy: "test-user");
        await _registry.CreateConnectionAsync(connection);
        Assert.True(await _registry.DeleteConnectionAsync(connection.ConnectionId));

        connection.Description = "after delete";

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _registry.UpdateConnectionAsync(connection));
    }
}
