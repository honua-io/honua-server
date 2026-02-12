// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Admin.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for admin endpoints.
/// </summary>
[Protocol(Protocols.Admin)]
[Collection("Database")]
public sealed class AdminEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.TableDiscovery)]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WithValidConnectionId_ReturnsTablesSuccessfully()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var tableDiscoveryResponse = JsonSerializer.Deserialize<TableDiscoveryResponse>(
            content, TableDiscoveryJsonContext.Default.TableDiscoveryResponse);
        tableDiscoveryResponse.Should().NotBeNull();
        tableDiscoveryResponse!.Tables.Should().NotBeNull();

        // Tables list may be empty if no PostGIS tables exist, which is fine
        // Each table should have required properties if any exist
        foreach (var table in tableDiscoveryResponse.Tables)
        {
            table.Schema.Should().NotBeNullOrEmpty();
            table.Table.Should().NotBeNullOrEmpty();
        }
    }

    [IntegrationTest]
    [Operation(Operations.TableDiscovery)]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WithInvalidConnectionId_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/connections/nonexistent/tables");

        // Assert - Should return 404 for connections that don't exist
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.TableDiscovery)]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    [Endpoint("GET /api/v1/admin/connections/tables")]
    [Endpoint("GET /api/v1/admin/connections/{*path}")]
    public async Task GetConnectionTables_WithEmptyConnectionId_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/connections//tables");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.TableDiscovery)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WithWrongHttpMethod_Returns405()
    {
        // Act
        var response = await _fixture.Client.PostAsync("/api/v1/admin/connections/test/tables", null);

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfiguration_ReturnsConfigurationDocumentation()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/config");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var configDoc = JsonSerializer.Deserialize<ConfigurationDocumentation>(
            content, ConfigurationJsonContext.Default.ConfigurationDocumentation);
        configDoc.Should().NotBeNull();
        configDoc!.Sections.Should().NotBeNull();
        configDoc.Sections.Should().NotBeEmpty();
        configDoc.EnvironmentVariables.Should().NotBeNull();
        configDoc.EnvironmentVariables.Should().NotBeEmpty();
        configDoc.Version.Should().NotBeNullOrEmpty();
        configDoc.Environment.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfiguration_ContainsRequiredSections()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/config");
        var content = await response.Content.ReadAsStringAsync();
        var configDoc = JsonSerializer.Deserialize<ConfigurationDocumentation>(
            content, ConfigurationJsonContext.Default.ConfigurationDocumentation);

        // Assert - verify required sections exist
        var sectionNames = configDoc!.Sections.Select(s => s.Name).ToList();
        sectionNames.Should().Contain("Features");
        sectionNames.Should().Contain("Database");
        sectionNames.Should().Contain("Cache");
        sectionNames.Should().Contain("Limits.Query");
        sectionNames.Should().Contain("Security");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfiguration_SensitiveValuesMasked()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/config");
        var content = await response.Content.ReadAsStringAsync();
        var configDoc = JsonSerializer.Deserialize<ConfigurationDocumentation>(
            content, ConfigurationJsonContext.Default.ConfigurationDocumentation);

        // Assert - verify sensitive properties are marked correctly
        var databaseSection = configDoc!.Sections.FirstOrDefault(s => s.Name == "Database");
        databaseSection.Should().NotBeNull();

        var connectionStringProp = databaseSection!.Properties
            .FirstOrDefault(p => p.Name == "DefaultConnection");
        connectionStringProp.Should().NotBeNull();
        connectionStringProp!.IsSensitive.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfiguration_ContainsEnvironmentVariableReference()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/v1/admin/config");
        var content = await response.Content.ReadAsStringAsync();
        var configDoc = JsonSerializer.Deserialize<ConfigurationDocumentation>(
            content, ConfigurationJsonContext.Default.ConfigurationDocumentation);

        // Assert - verify environment variable quick reference is populated
        var envVars = configDoc!.EnvironmentVariables;
        envVars.Should().Contain(e => e.Name == "ConnectionStrings__DefaultConnection");
        envVars.Should().Contain(e => e.Name == "HONUA_ADMIN_UI");
        envVars.Should().Contain(e => e.Name == "Cache__Enabled");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/config")]
    public async Task GetConfiguration_WithWrongHttpMethod_Returns405()
    {
        // Act
        var response = await _fixture.Client.PostAsync("/api/v1/admin/config", null);

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }
}
