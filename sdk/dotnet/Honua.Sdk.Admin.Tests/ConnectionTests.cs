// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Sdk.Admin.Models;
using Honua.Sdk.Admin.Tests.Fixtures;

namespace Honua.Sdk.Admin.Tests;

public sealed class ConnectionTests
{
    private static readonly Guid TestConnectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static object CreateConnectionSummary(Guid? id = null) => new
    {
        connectionId = (id ?? TestConnectionId).ToString(),
        name = "test-conn",
        host = "localhost",
        port = 5432,
        databaseName = "testdb",
        username = "admin",
        sslRequired = true,
        sslMode = "Require",
        storageType = "managed",
        isActive = true,
        healthStatus = "Healthy",
        createdAt = DateTimeOffset.UtcNow,
        createdBy = "admin"
    };

    [Fact]
    public async Task ListConnectionsAsync_ReturnsConnections()
    {
        var connections = new[] { CreateConnectionSummary() };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/connections/", req.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Get, req.Method);
            return Task.FromResult(TestHelpers.CreateJsonResponse(connections));
        });

        var result = await client.ListConnectionsAsync();

        Assert.Single(result);
        Assert.Equal("test-conn", result[0].Name);
    }

    [Fact]
    public async Task GetConnectionAsync_ReturnsDetail()
    {
        var detail = new
        {
            connectionId = TestConnectionId.ToString(),
            name = "test-conn",
            host = "localhost",
            port = 5432,
            databaseName = "testdb",
            username = "admin",
            sslRequired = true,
            sslMode = "Require",
            storageType = "managed",
            isActive = true,
            healthStatus = "Healthy",
            createdAt = DateTimeOffset.UtcNow,
            createdBy = "admin",
            encryptionVersion = 1,
            updatedAt = DateTimeOffset.UtcNow
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains($"/admin/connections/{TestConnectionId}", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(detail));
        });

        var result = await client.GetConnectionAsync(TestConnectionId.ToString());

        Assert.Equal("test-conn", result.Name);
        Assert.Equal(1, result.EncryptionVersion);
    }

    [Fact]
    public async Task CreateConnectionAsync_SendsPostAndReturnsSummary()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/admin/connections/", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(CreateConnectionSummary(), HttpStatusCode.Created));
        });

        var result = await client.CreateConnectionAsync(new CreateSecureConnectionRequest
        {
            Name = "test-conn",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "admin",
            Password = "secret"
        });

        Assert.Equal("test-conn", result.Name);
    }

    [Fact]
    public async Task UpdateConnectionAsync_SendsPutAndReturnsSummary()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains($"/admin/connections/{TestConnectionId}", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(CreateConnectionSummary()));
        });

        var result = await client.UpdateConnectionAsync(TestConnectionId.ToString(), new UpdateSecureConnectionRequest
        {
            Description = "Updated connection"
        });

        Assert.Equal("test-conn", result.Name);
    }

    [Fact]
    public async Task DeleteConnectionAsync_SendsDelete()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            Assert.Contains($"/admin/connections/{TestConnectionId}", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(new { }, HttpStatusCode.OK));
        });

        await client.DeleteConnectionAsync(TestConnectionId.ToString());
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsResult()
    {
        var testResult = new
        {
            connectionId = TestConnectionId.ToString(),
            connectionName = "test-conn",
            isHealthy = true,
            testedAt = DateTimeOffset.UtcNow,
            message = "Connection is healthy"
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains($"/admin/connections/{TestConnectionId}/test", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(testResult));
        });

        var result = await client.TestConnectionAsync(TestConnectionId.ToString());

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task TestDraftConnectionAsync_ReturnsResult()
    {
        var testResult = new
        {
            connectionId = Guid.Empty.ToString(),
            connectionName = "draft",
            isHealthy = false,
            testedAt = DateTimeOffset.UtcNow,
            message = "Connection test failed"
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/admin/connections/test", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(testResult));
        });

        var result = await client.TestDraftConnectionAsync(new CreateSecureConnectionRequest
        {
            Name = "draft",
            Host = "bad-host",
            DatabaseName = "db",
            Username = "user",
            Password = "pass"
        });

        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task ValidateEncryptionAsync_ReturnsResult()
    {
        var validationResult = new
        {
            isValid = true,
            currentKeyVersion = 2,
            validatedAt = DateTimeOffset.UtcNow,
            message = "Encryption service is working correctly"
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/admin/connections/encryption/validate", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(validationResult));
        });

        var result = await client.ValidateEncryptionAsync();

        Assert.True(result.IsValid);
        Assert.Equal(2, result.CurrentKeyVersion);
    }

    [Fact]
    public async Task RotateEncryptionKeyAsync_ReturnsResult()
    {
        var rotationResult = new
        {
            previousKeyVersion = 1,
            newKeyVersion = 2,
            rotatedAt = DateTimeOffset.UtcNow,
            message = "Key rotated"
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/admin/connections/encryption/rotate-key", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(rotationResult));
        });

        var result = await client.RotateEncryptionKeyAsync();

        Assert.Equal(1, result.PreviousKeyVersion);
        Assert.Equal(2, result.NewKeyVersion);
    }
}
