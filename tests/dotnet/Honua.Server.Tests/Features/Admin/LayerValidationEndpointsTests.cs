// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin layer storage validation endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerValidationEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private string _schema = string.Empty;
    private string _tableName = string.Empty;
    private string _serviceName = string.Empty;
    private int? _layerId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
        _schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Test schema was not initialized.");
        _tableName = $"layer_validation_{Guid.NewGuid():N}";
        _serviceName = $"svc_validation_{Guid.NewGuid():N}";

        await CreateSpatialTableAsync();
        await CreateLayerMetadataAsync();
    }

    public async Task DisposeAsync()
    {
        await CleanupLayerMetadataAsync();
        await DropSpatialTableAsync();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/validation")]
    public async Task GetLayerValidation_WithMatchingStorage_ReturnsValid()
    {
        var response = await _client.GetAsync($"/api/v1/admin/metadata/layers/{_layerId}/validation");

        response.Be200Ok();
        var apiResponse = await ReadValidationResponseAsync(response);

        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.IsValid.Should().BeTrue();
        apiResponse.Data.Status.Should().Be("valid");
        apiResponse.Data.StorageSchema.Should().Be(_schema);
        apiResponse.Data.StorageTable.Should().Be(_tableName);
        apiResponse.Data.Checks.Should().NotContain(check => check.Severity == "error");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/validation")]
    public async Task GetLayerValidation_WhenDeclaredColumnIsDropped_ReturnsInvalid()
    {
        await DropPopulationColumnAsync();

        var response = await _client.GetAsync($"/api/v1/admin/metadata/layers/{_layerId}/validation");

        response.Be200Ok();
        var apiResponse = await ReadValidationResponseAsync(response);

        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.IsValid.Should().BeFalse();
        apiResponse.Data.Status.Should().Be("invalid");
        apiResponse.Data.Checks.Should().Contain(check =>
            check.Code == "declared-field" &&
            check.Severity == "error" &&
            check.Expected == "population" &&
            check.Actual == null);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/validation")]
    public async Task GetLayerValidation_WithInvalidSavedPermanentFilter_ReturnsInvalid()
    {
        await SetLayerPermanentFilterMetadataAsync("missing_field = 'test'", "arcgis-sql");

        var response = await _client.GetAsync($"/api/v1/admin/metadata/layers/{_layerId}/validation");

        response.Be200Ok();
        var apiResponse = await ReadValidationResponseAsync(response);

        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.IsValid.Should().BeFalse();
        apiResponse.Data.Status.Should().Be("invalid");
        apiResponse.Data.Checks.Should().Contain(check =>
            check.Code == "permanent-filter" &&
            check.Severity == "error" &&
            check.Expected == "missing_field = 'test'" &&
            check.Actual != null &&
            check.Actual.Contains("missing_field", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ApiResponse<LayerValidationResponse>> ReadValidationResponseAsync(
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            LayerValidationJsonContext.Default.ApiResponseLayerValidationResponse);

        apiResponse.Should().NotBeNull($"response payload was: {payload}");
        return apiResponse!;
    }

    private async Task CreateSpatialTableAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {QuoteIdentifier(_tableName)} (
                id integer PRIMARY KEY,
                name text NOT NULL,
                population integer,
                geom geometry(Point, 4326) NOT NULL
            );

            INSERT INTO {QuoteIdentifier(_tableName)} (id, name, population, geom)
            VALUES
                (1, 'Feature One', 100, ST_SetSRID(ST_Point(-157.8583, 21.3069), 4326)),
                (2, 'Feature Two', 250, ST_SetSRID(ST_Point(-157.8167, 21.2970), 4326));
            """;

        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateLayerMetadataAsync()
    {
        _layerId = Random.Shared.Next(100_000, int.MaxValue);
        await UpsertLayerValidationMetadataAsync();
    }

    private async Task DropPopulationColumnAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {QuoteIdentifier(_tableName)} DROP COLUMN population;";

        await command.ExecuteNonQueryAsync();
    }

    private async Task SetLayerPermanentFilterMetadataAsync(string expression, string language)
        => await UpsertLayerValidationMetadataAsync(new MetadataV2PermanentFilter
        {
            Expression = expression,
            Language = language
        });

    private async Task UpsertLayerValidationMetadataAsync(MetadataV2PermanentFilter? permanentFilter = null)
    {
        var provider = _fixture.GetService<IMetadataV2GraphProvider>() as TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException("Test V2 graph provider was not registered.");
        var snapshot = await provider.GetCurrentAsync();
        var layerId = _layerId!.Value;
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        var resourceId = $"resource.layer-validation.{layerId}";
        var bindingId = $"storage.layer-validation.{layerId}";
        var serviceId = $"service.layer-validation.{layerId}";
        var publicationId = $"publication.layer-validation.{layerId}";
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = resourceId,
                Name = $"Layer {_tableName}"
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "id",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = false },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer, Nullable = true },
                new MetadataV2Field
                {
                    Name = "geom",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                StorageCrs = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom"
            },
            PermanentFilter = permanentFilter
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = bindingId,
                Name = bindingId
            },
            ResourceId = resourceId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{_schema}.{_tableName}",
            StorageLayerId = layerId
        };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = serviceId,
                Name = _serviceName
            },
            Route = "/api/v1/admin/metadata/layers"
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = publicationId,
                Name = layerIdText
            },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = bindingId,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerIdText,
                IsNumeric = true
            },
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
            IsPrimary = true
        };

        provider.SetGraph(snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources
                .Where(existing => !string.Equals(existing.Metadata.Id, resourceId, StringComparison.Ordinal))
                .Append(resource)
                .ToArray(),
            StorageBindings = snapshot.Graph.StorageBindings
                .Where(existing => !string.Equals(existing.Metadata.Id, bindingId, StringComparison.Ordinal))
                .Append(storageBinding)
                .ToArray(),
            Services = snapshot.Graph.Services
                .Where(existing => !string.Equals(existing.Metadata.Id, serviceId, StringComparison.Ordinal))
                .Append(service)
                .ToArray(),
            Publications = snapshot.Graph.Publications
                .Where(existing => !string.Equals(existing.Metadata.Id, publicationId, StringComparison.Ordinal))
                .Append(publication)
                .ToArray(),
            Revision = snapshot.Graph.Revision + 1
        });
    }

    private async Task CleanupLayerMetadataAsync()
    {
        if (!_layerId.HasValue)
        {
            return;
        }

        var provider = _fixture.GetService<IMetadataV2GraphProvider>() as TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException("Test V2 graph provider was not registered.");
        var snapshot = await provider.GetCurrentAsync();
        var layerId = _layerId.Value.ToString(CultureInfo.InvariantCulture);
        var suffix = $".layer-validation.{layerId}";
        provider.SetGraph(snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources
                .Where(existing => !existing.Metadata.Id.EndsWith(suffix, StringComparison.Ordinal))
                .ToArray(),
            StorageBindings = snapshot.Graph.StorageBindings
                .Where(existing => !existing.Metadata.Id.EndsWith(suffix, StringComparison.Ordinal))
                .ToArray(),
            Services = snapshot.Graph.Services
                .Where(existing => !existing.Metadata.Id.EndsWith(suffix, StringComparison.Ordinal))
                .ToArray(),
            Publications = snapshot.Graph.Publications
                .Where(existing => !existing.Metadata.Id.EndsWith(suffix, StringComparison.Ordinal))
                .ToArray(),
            Revision = snapshot.Graph.Revision + 1
        });
    }

    private async Task DropSpatialTableAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableName))
        {
            return;
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(_tableName)};";

        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.Any(static ch => !char.IsLetterOrDigit(ch) && ch != '_'))
        {
            throw new ArgumentException($"Invalid identifier '{identifier}'.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
