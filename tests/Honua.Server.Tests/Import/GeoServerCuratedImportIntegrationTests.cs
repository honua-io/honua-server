// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests that verify GeoServer discovery against deterministic seeded resources.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoServerCuratedImportIntegrationTests : IAsyncLifetime
{
    private readonly GeoServerFixture _geoServer = new(seedCuratedData: true);
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public GeoServerCuratedImportIntegrationTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [GeoServerImportExecutionSettings.AllowUnsafeLocalUrlsInTestEnvironmentKey] = bool.TrueString
                });
            }));
    }

    public async Task InitializeAsync()
    {
        await _geoServer.InitializeAsync();
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _geoServer.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_CuratedHarness_ReturnsSeededWorkspaceResources()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new
        {
            GeoServerRestUrl = _geoServer.RestUrl,
            Username = _geoServer.Username,
            Password = _geoServer.Password,
            IncludeCompatibilityAnalysis = true,
            IncludeStyleContent = true,
            TimeoutSeconds = 120
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var workspaceNames = payload!.RootElement.GetProperty("workspaces").EnumerateArray()
            .Select(element => element.GetProperty("name").GetString())
            .ToArray();

        workspaceNames.Should().Contain(GeoServerFixture.CuratedWorkspaceName);
        workspaceNames.Should().Contain(GeoServerFixture.EmptyWorkspaceName);

        payload.RootElement.GetProperty("dataStores").EnumerateArray()
            .Where(element => element.GetProperty("workspaceName").GetString() == GeoServerFixture.EmptyWorkspaceName)
            .Should().BeEmpty();

        payload.RootElement.GetProperty("layers").EnumerateArray()
            .Where(element => element.GetProperty("workspaceName").GetString() == GeoServerFixture.EmptyWorkspaceName)
            .Should().BeEmpty();

        var dataStore = payload.RootElement.GetProperty("dataStores").EnumerateArray()
            .Single(element =>
                element.GetProperty("workspaceName").GetString() == GeoServerFixture.CuratedWorkspaceName &&
                element.GetProperty("name").GetString() == GeoServerFixture.CuratedDataStoreName);

        dataStore.GetProperty("type").GetString().Should().Be("Shapefile");

        var layer = payload.RootElement.GetProperty("layers").EnumerateArray()
            .Single(element =>
                element.GetProperty("workspaceName").GetString() == GeoServerFixture.CuratedWorkspaceName &&
                element.GetProperty("name").GetString() == GeoServerFixture.CuratedLayerName);

        layer.GetProperty("dataStoreName").GetString().Should().Be(GeoServerFixture.CuratedDataStoreName);
        layer.GetProperty("defaultStyle").GetString().Should().Be(GeoServerFixture.CuratedQualifiedDefaultStyleName);
        layer.GetProperty("alternativeStyles").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Contain(GeoServerFixture.CuratedAlternativeStyleName);

        var style = payload.RootElement.GetProperty("styles").EnumerateArray()
            .Single(element =>
                GetOptionalStringProperty(element, "workspaceName") == GeoServerFixture.CuratedWorkspaceName &&
                element.GetProperty("name").GetString() == GeoServerFixture.CuratedWorkspaceStyleName);

        style.GetProperty("format").GetString().Should().Be("sld");
        style.GetProperty("sldContent").GetString().Should().Contain("#f36f21");

        var layerGroup = payload.RootElement.GetProperty("layerGroups").EnumerateArray()
            .Single(element =>
                GetOptionalStringProperty(element, "workspaceName") == GeoServerFixture.CuratedWorkspaceName &&
                element.GetProperty("name").GetString() == GeoServerFixture.CuratedLayerGroupName);

        layerGroup.GetProperty("layers").EnumerateArray()
            .Select(element => element.GetProperty("name").GetString())
            .Should().Contain(GeoServerFixture.CuratedQualifiedLayerName);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/translate")]
    public async Task Translate_CuratedHarness_ReturnsDeterministicUnsupportedManifest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/translate", new
        {
            GeoServerRestUrl = _geoServer.RestUrl,
            Username = _geoServer.Username,
            Password = _geoServer.Password,
            WorkspaceNames = new[] { GeoServerFixture.CuratedWorkspaceName },
            ImportStyles = true,
            IncludeStyleContent = true,
            RequestTimeoutSeconds = 120
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        payload!.RootElement.GetProperty("apiVersion").GetString().Should().Be(MigrationManifestVersions.V1Alpha1);
        payload.RootElement.GetProperty("manifestHash").GetString().Should().NotBeNullOrWhiteSpace();
        payload.RootElement.GetProperty("connectionDrafts").GetArrayLength().Should().Be(0);
        payload.RootElement.GetProperty("publishPlan").GetArrayLength().Should().Be(0);

        payload.RootElement.GetProperty("stylePlan").GetArrayLength().Should().BeGreaterThan(0);

        var workspaceStylePlan = payload.RootElement.GetProperty("stylePlan").EnumerateArray()
            .Single(element => element.GetProperty("sourceStyleName").GetString() == GeoServerFixture.CuratedWorkspaceStyleName);
        workspaceStylePlan.GetProperty("translationStatus").GetString().Should().Be("ManualActionRequired");
        workspaceStylePlan.GetProperty("diagnosticCodes").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Contain(MigrationReasonCodes.UnsupportedSldStyle);
        workspaceStylePlan.GetProperty("sourceContent").GetString().Should().Contain("#f36f21");

        payload.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(element => element.GetProperty("code").GetString())
            .Should().Contain(
            [
                MigrationReasonCodes.UnsupportedDatastoreType,
                MigrationReasonCodes.UnsupportedLayerSource,
                MigrationReasonCodes.UnsupportedLayerGroup,
                MigrationReasonCodes.UnsupportedSldStyle
            ]);
    }

    private static string? GetOptionalStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
