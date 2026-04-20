// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Net;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Admin.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

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
    [Endpoint("PUT /api/v1/admin/connections/{id}/tables")]
    [Endpoint("DELETE /api/v1/admin/connections/{id}/tables")]
    [Endpoint("PATCH /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WithWrongHttpMethods_Returns405()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _fixture.Client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/connections/test/tables"));

            response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
        }
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
        sectionNames.Should().Contain("TemporaryFiles");
        sectionNames.Should().Contain("FeatureChangeEvents");
        sectionNames.Should().Contain("FeatureStreaming");
        sectionNames.Should().Contain("ManifestApproval");
        sectionNames.Should().Contain("GitOpsWatch");
        sectionNames.Should().Contain("Geoprocessing:Workspace");
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
        envVars.Should().Contain(e => e.Name == "Cache__Enabled");
        envVars.Should().Contain(e => e.Name == "HONUA_ENABLE_BASIC_AUTH_COMPAT");
        envVars.Should().Contain(e => e.Name == "HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH");
        envVars.Should().Contain(e => e.Name == "HONUA__ADAPTIVESAMPLING__BASESAMPLINGRATE");
        envVars.Should().Contain(e => e.Name == "HONUA__ADAPTIVESAMPLING__MINSAMPLINGRATE");
        envVars.Should().Contain(e => e.Name == "HONUA__ADAPTIVESAMPLING__MAXSAMPLINGRATE");
        envVars.Should().Contain(e => e.Name == "HONUA__ADAPTIVESAMPLING__LOAD__ACTIVEREQUESTTHRESHOLD");
        envVars.Should().Contain(e => e.Name == "Geoprocessing__Workspace__CleanupInterval");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfiguration_WhenPublicBaseUrlProvidedViaAlias_UsesEnvironmentSourceAndCurrentValue()
    {
        var isolatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
                builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PUBLIC_BASE_URL"] = "https://api.public.example.test"
                    })));

        try
        {
            await isolatedFixture.InitializeAsync();

            var response = await isolatedFixture.Client.GetAsync("/api/v1/admin/config");
            var content = await response.Content.ReadAsStringAsync();
            var configDoc = JsonSerializer.Deserialize<ConfigurationDocumentation>(
                content,
                ConfigurationJsonContext.Default.ConfigurationDocumentation);

            var networkingSection = configDoc!.Sections.Single(section => section.Name == "Networking");
            var baseUrlProperty = networkingSection.Properties.Single(property => property.Path == "Public:BaseUrl");

            baseUrlProperty.CurrentValue?.ToString().Should().Be("https://api.public.example.test");
            baseUrlProperty.Source.Should().Be("Environment");
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfiguration_WorkspaceEnvVarsHaveDefaults()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/config");
        var content = await response.Content.ReadAsStringAsync();
        var configDoc = JsonSerializer.Deserialize<ConfigurationDocumentation>(
            content, ConfigurationJsonContext.Default.ConfigurationDocumentation);

        var envVars = configDoc!.EnvironmentVariables;

        envVars.Should().Contain(e =>
            e.Name == "Geoprocessing__Workspace__ScratchDefaultTtl" && e.Default == "01:00:00");
        envVars.Should().Contain(e =>
            e.Name == "Geoprocessing__Workspace__TempLayerDefaultTtl" && e.Default == "1.00:00:00");
        envVars.Should().Contain(e =>
            e.Name == "Geoprocessing__Workspace__ResultCollectionDefaultTtl" && e.Default == "7.00:00:00");
        envVars.Should().Contain(e =>
            e.Name == "Geoprocessing__Workspace__MaxWorkspaceCount" && e.Default == "100");
        envVars.Should().Contain(e =>
            e.Name == "Geoprocessing__Workspace__MaxArtifactCount" && e.Default == "1000");
        envVars.Should().Contain(e =>
            e.Name == "Geoprocessing__Workspace__MaxStorageBytes" && e.Default == "10737418240");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /admin/index.html")]
    [Endpoint("GET /admin/_framework/blazor.webassembly.js")]
    public async Task GetAdminUiAssets_NotServedByServer()
    {
        // The in-tree Blazor admin UI was removed; admin UI now lives in the
        // sibling honua-server-admin repo and is deployed separately. Verify
        // the server returns 404 for the /admin/* prefix to catch any
        // accidental re-introduction of the hosted shell.
        using var shellResponse = await _fixture.Client.GetAsync("/admin/index.html");
        using var frameworkResponse = await _fixture.Client.GetAsync("/admin/_framework/blazor.webassembly.js");

        shellResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        frameworkResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/v1/admin/openapi.json")]
    public async Task GetAdminOpenApiSpec_ReturnsValidOpenApiSpecification()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/openapi.json");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("application/vnd.oai.openapi+json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.0");
        json.RootElement.TryGetProperty("paths", out var pathsElement).Should().BeTrue();

        var paths = pathsElement;
        paths.TryGetProperty("/capabilities", out _).Should().BeTrue();
        paths.TryGetProperty("/openapi.json", out _).Should().BeTrue();
        paths.TryGetProperty("/services", out _).Should().BeTrue();
        paths.TryGetProperty("/services/{serviceName}/settings", out _).Should().BeTrue();
        paths.TryGetProperty("/services/{serviceName}/access-policy", out _).Should().BeTrue();
        paths.TryGetProperty("/services/{serviceName}/timeinfo", out _).Should().BeTrue();
        paths.TryGetProperty("/services/{serviceName}/layers/{layerId}/metadata", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/config")]
    [Endpoint("PUT /api/v1/admin/config")]
    [Endpoint("DELETE /api/v1/admin/config")]
    [Endpoint("PATCH /api/v1/admin/config")]
    public async Task GetConfiguration_WithWrongHttpMethods_Returns405()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _fixture.Client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/config"));

            response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /api/v1/admin/openapi.json")]
    [Endpoint("PUT /api/v1/admin/openapi.json")]
    [Endpoint("DELETE /api/v1/admin/openapi.json")]
    [Endpoint("PATCH /api/v1/admin/openapi.json")]
    public async Task GetAdminOpenApiSpec_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _fixture.Client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/openapi.json"));

            response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
            (response.Headers.TryGetValues("Allow", out var allowedValues) ||
             response.Content.Headers.TryGetValues("Allow", out allowedValues))
                .Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }
}
