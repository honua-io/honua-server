// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for secure connection admin endpoints.
/// </summary>
/// <remarks>
/// Tests cover:
/// - Admin API authorization requirements
/// - CRUD operations for secure connections
/// - Security validation and error handling
/// - Data sanitization in responses
/// </remarks>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class SecureConnectionEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "secure-connection-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;
    private ISecureConnectionRegistry _registry = null!;

    public SecureConnectionEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        using var scope = _fixture.Services.CreateScope();
        _registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections")]
    public async Task GetConnections_WithAdminAuth_ReturnsConnections()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/connections");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<SecureConnectionSummary[]>>(jsonResponse, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections")]
    public async Task GetConnections_WithoutAdminAuth_ReturnsUnauthorized()
    {
        // Arrange
        using var unauthorizedClient = _fixture.CreateClient(); // No admin auth

        // Act
        var response = await unauthorizedClient.GetAsync("/api/v1/admin/connections");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_ValidEncryptedConnection_ReturnsCreated()
    {
        // Arrange
        var request = new CreateSecureConnectionRequest
        {
            Name = $"test-encrypted-{Guid.NewGuid():N}",
            Description = "Test encrypted connection",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Provider = "PostgreSQL",
            Password = "testpassword123",
            SslRequired = true,
            SslMode = "Require"
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/connections", content);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<SecureConnectionSummary>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.Equal(request.Name, apiResponse.Data.Name);
        Assert.Equal(request.Host, apiResponse.Data.Host);
        Assert.Equal("postgresql", apiResponse.Data.Provider);
        Assert.Equal("managed", apiResponse.Data.StorageType);

        // Verify location header
        Assert.NotNull(response.Headers.Location);
        Assert.Contains(apiResponse.Data.ConnectionId.ToString(), response.Headers.Location.ToString());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_ValidSecretRefConnection_ReturnsCreated()
    {
        // Arrange
        var request = new CreateSecureConnectionRequest
        {
            Name = $"test-secretref-{Guid.NewGuid():N}",
            Description = "Test secret reference connection",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            SecretReference = "env:TEST_DB_CONNECTION",
            SecretType = "environment",
            SslRequired = true,
            SslMode = "Require"
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/connections", content);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<SecureConnectionSummary>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.Equal("external", apiResponse.Data.StorageType);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_InvalidRequest_ReturnsBadRequest()
    {
        // Arrange - Request with both password and secret reference (invalid)
        var request = new CreateSecureConnectionRequest
        {
            Name = "invalid-connection",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Password = "password123",
            SecretReference = "env:SECRET",
            SecretType = "environment",
            SslRequired = true,
            SslMode = "Require"
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/connections", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [Endpoint("POST /api/v1/admin/connections")]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    public async Task CreateConnection_SslRequiredRejectsFallbackSslModes(string sslMode)
    {
        var request = new CreateSecureConnectionRequest
        {
            Name = $"test-invalid-ssl-{Guid.NewGuid():N}",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Password = "testpassword123",
            SslRequired = true,
            SslMode = sslMode
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/admin/connections", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("SSL mode must require encrypted transport when SSL is required", responseJson, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}")]
    public async Task GetConnection_ExistingConnection_ReturnsDetail()
    {
        // Arrange - Create a test connection first
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-get-detail-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        // Act
        var response = await _client.GetAsync($"/api/v1/admin/connections/{created.ConnectionId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<SecureConnectionDetail>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.Equal(created.ConnectionId, apiResponse.Data.ConnectionId);
        Assert.Equal(created.Name, apiResponse.Data.Name);

        // Verify sensitive data is not exposed
        Assert.DoesNotContain("password", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}")]
    public async Task GetConnection_NonExistentConnection_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/admin/connections/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/{id}/test")]
    public async Task TestConnection_ValidConnection_ReturnsTestResult()
    {
        // Arrange - Create a test connection
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-health-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        // Act
        var response = await _client.PostAsync($"/api/v1/admin/connections/{created.ConnectionId}/test", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<ConnectionTestResult>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.Equal(created.ConnectionId, apiResponse.Data.ConnectionId);
        Assert.Equal(created.Name, apiResponse.Data.ConnectionName);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/test")]
    public async Task TestDraftConnection_WithValidRequest_ReturnsResultOrBadRequest()
    {
        var request = new CreateSecureConnectionRequest
        {
            Name = $"draft-test-{Guid.NewGuid():N}",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Password = "testpassword123",
            SslRequired = true,
            SslMode = "Require"
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/admin/connections/test", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/test")]
    public async Task TestDraftConnection_UsesRequestedSslMode_WhenBuildingConnectionString()
    {
        // Arrange
        var capturingBuilder = new CapturingConnectionStringBuilder();
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
                services.RemoveAll<IDatabaseConnectionStringBuilder>();
                services.AddSingleton<IDatabaseConnectionStringBuilder>(capturingBuilder);

                services.RemoveAll<IConnectionHealthTester>();
                services.AddSingleton<IConnectionHealthTester>(new AlwaysHealthyConnectionTester());
            });

        try
        {
            await localFixture.InitializeAsync();
            using var client = localFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            var request = new CreateSecureConnectionRequest
            {
                Name = $"draft-sslmode-{Guid.NewGuid():N}",
                Host = "localhost",
                Port = 5432,
                DatabaseName = "testdb",
                Username = "testuser",
                Password = "testpassword123",
                SslRequired = true,
                SslMode = "VerifyFull"
            };

            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Act
            var response = await client.PostAsync("/api/v1/admin/connections/test", content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capturingBuilder.LastSslMode.Should().Be(SslMode.VerifyFull);
        }
        finally
        {
            await localFixture.DisposeAsync();
        }
    }

    [Theory]
    [Trait("Category", "Integration")]
    [Endpoint("POST /api/v1/admin/connections/test")]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    public async Task TestDraftConnection_SslRequiredRejectsFallbackSslModes(string sslMode)
    {
        var request = new CreateSecureConnectionRequest
        {
            Name = $"draft-invalid-ssl-{Guid.NewGuid():N}",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Password = "testpassword123",
            SslRequired = true,
            SslMode = sslMode
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/admin/connections/test", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("SSL mode must require encrypted transport when SSL is required", responseJson, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/encryption/validate")]
    public async Task ValidateEncryption_WithAdminAuth_ReturnsValidationResult()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/connections/encryption/validate", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<EncryptionValidationResult>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.True(apiResponse.Data.IsValid); // Should be valid in test environment
        Assert.True(apiResponse.Data.CurrentKeyVersion > 0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_DuplicateName_ReturnsBadRequest()
    {
        // Arrange - Create first connection
        var firstRequest = new CreateSecureConnectionRequest
        {
            Name = $"test-duplicate-{Guid.NewGuid():N}",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Password = "testpassword123",
            SslRequired = true,
            SslMode = "Require"
        };

        var jsonContent = JsonSerializer.Serialize(firstRequest, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var firstResponse = await _client.PostAsync("/api/v1/admin/connections", content);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        // Act - Try to create second connection with same name
        var secondResponse = await _client.PostAsync("/api/v1/admin/connections", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task CreateConnection_WhenEncryptionServiceThrowsInvalidOperation_ReturnsInternalServerError()
    {
        const string sensitiveError = "encryption failed secret=abc123";

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
                services.RemoveAll<IConnectionEncryptionService>();
                services.AddSingleton<IConnectionEncryptionService>(
                    new ThrowingEncryptionService(
                        new InvalidOperationException(sensitiveError),
                        failureTrigger: "testpassword123"));
            });

        await localFixture.InitializeAsync();

        try
        {
            using var client = localFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            var request = new CreateSecureConnectionRequest
            {
                Name = $"test-encryption-failure-{Guid.NewGuid():N}",
                Host = "localhost",
                Port = 5432,
                DatabaseName = "testdb",
                Username = "testuser",
                Password = "testpassword123",
                SslRequired = true,
                SslMode = "Require"
            };

            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/api/v1/admin/connections", content);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var responseBody = await response.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(sensitiveError);
            responseBody.Should().NotContain("secret=abc123");
        }
        finally
        {
            await localFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    [Endpoint("GET /api/v1/admin/connections")]
    public async Task Endpoints_ResponsesSanitized_NoSensitiveDataExposed()
    {
        // This test ensures that API responses never contain sensitive information
        // like passwords, encryption keys, or actual secret values

        // Arrange - Create connection with password
        var request = new CreateSecureConnectionRequest
        {
            Name = $"test-sanitized-{Guid.NewGuid():N}",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            Password = "super-secret-password-123!@#",
            SslRequired = true,
            SslMode = "Require"
        };

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var createResponse = await _client.PostAsync("/api/v1/admin/connections", content);
        var createResponseJson = await createResponse.Content.ReadAsStringAsync();

        var listResponse = await _client.GetAsync("/api/v1/admin/connections");
        var listResponseJson = await listResponse.Content.ReadAsStringAsync();

        // Assert - No sensitive data should appear in any response
        var sensitiveTerms = new[] { "super-secret-password", "password", "secret", "encrypted", "key" };

        foreach (var term in sensitiveTerms)
        {
            Assert.DoesNotContain(term, createResponseJson, StringComparison.OrdinalIgnoreCase);
        }

        // The password should not appear in the list response
        Assert.DoesNotContain("super-secret-password", listResponseJson, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    public async Task UpdateConnection_ExistingConnection_ReturnsUpdatedSummary()
    {
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-update-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        var updateRequest = new UpdateSecureConnectionRequest
        {
            Description = "Updated description",
            IsActive = false
        };

        var jsonContent = JsonSerializer.Serialize(updateRequest, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync($"/api/v1/admin/connections/{created.ConnectionId}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<SecureConnectionSummary>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.Equal("Updated description", apiResponse.Data.Description);
        Assert.False(apiResponse.Data.IsActive);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    public async Task UpdateConnection_SslRequiredRejectsFallbackSslModes(string sslMode)
    {
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-update-invalid-ssl-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: [1, 2, 3, 4, 5],
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        var updateRequest = new UpdateSecureConnectionRequest
        {
            SslMode = sslMode,
            SslRequired = true
        };

        var jsonContent = JsonSerializer.Serialize(updateRequest, _jsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync($"/api/v1/admin/connections/{created.ConnectionId}", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("SSL mode must require encrypted transport when SSL is required", responseJson, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/connections/{id}")]
    public async Task DeleteConnection_ExistingConnection_ReturnsOk()
    {
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-delete-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 5, 4, 3, 2, 1 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        var response = await _client.DeleteAsync($"/api/v1/admin/connections/{created.ConnectionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deleted = await _registry.GetConnectionAsync(created.ConnectionId);
        Assert.Null(deleted);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/encryption/rotate-key")]
    public async Task RotateEncryptionKey_WithAdminAuth_ReturnsBadRequest_BecauseRotationNotSupported()
    {
        var response = await _client.PostAsync("/api/v1/admin/connections/encryption/rotate-key", null);

        // Key rotation is not supported at runtime (requires re-deployment with new master key)
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, _jsonOptions);

        Assert.NotNull(apiResponse);
        Assert.False(apiResponse.Success);
    }

    private sealed class CapturingConnectionStringBuilder : IDatabaseConnectionStringBuilder
    {
        public SslMode LastSslMode { get; private set; } = SslMode.Require;

        public string BuildConnectionString(
            string host,
            int port,
            string databaseName,
            string username,
            string password,
            SslMode sslMode)
        {
            LastSslMode = sslMode;
            return "Host=localhost;Port=5432;Database=test;Username=test;Password=test;SslMode=Require";
        }

        public Task<string> BuildConnectionStringAsync(DataConnection connection)
        {
            LastSslMode = connection.SslMode;
            return Task.FromResult(BuildConnectionString(
                connection.Host,
                connection.Port,
                connection.DatabaseName,
                connection.Username,
                password: string.Empty,
                connection.SslMode));
        }

        public bool ValidateConnectionString(string connectionString)
            => !string.IsNullOrWhiteSpace(connectionString);
    }

    private sealed class AlwaysHealthyConnectionTester : IConnectionHealthTester
    {
        public Task<ConnectionHealthStatus> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
            => Task.FromResult(ConnectionHealthStatus.Healthy);
    }

    private sealed class ThrowingEncryptionService(Exception exception, string failureTrigger) : IConnectionEncryptionService
    {
        private readonly Exception _exception = exception;
        private readonly string _failureTrigger = failureTrigger;

        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
        {
            if (connectionString.Contains(_failureTrigger, StringComparison.Ordinal))
            {
                return Task.FromException<byte[]>(_exception);
            }

            // Any deterministic payload is acceptable for tests that only assert
            // endpoint behavior and do not decrypt persisted connection strings.
            return Task.FromResult(Encoding.UTF8.GetBytes("test-encrypted-payload"));
        }

        public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
            => Task.FromException<string>(new NotSupportedException());

        public Task<int> GetCurrentKeyVersionAsync()
            => Task.FromResult(1);

        public Task<int> RotateKeyAsync()
            => Task.FromException<int>(new NotSupportedException());

        public Task<bool> ValidateEncryptionAsync()
            => Task.FromResult(false);
    }
}
