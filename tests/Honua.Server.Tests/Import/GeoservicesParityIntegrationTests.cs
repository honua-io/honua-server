// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// External parity checks that compare selected ArcGIS layer responses with imported Honua table data.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
[Operation(Operations.Query)]
public sealed class GeoservicesParityIntegrationTests : IAsyncLifetime, IDisposable
{
    private const string ExternalServicesEnv = "HONUA_TEST_ESRI_PARITY";
    private const int SampleFeatureCount = 15;
    private const int QueryMatrixPageSize = 50;
    private static readonly JsonSerializerOptions _scorecardJsonOptions = new() { WriteIndented = true };

    private static readonly ParityServiceCase[] _serviceCases =
    [
        new(
            Name: "hawaii_infra_dams",
            ServiceUrl: "https://geodata.hawaii.gov/arcgis/rest/services/Infrastructure/MapServer",
            LayerId: 10,
            CompareField: "dam_name",
            NumericField: "longitude",
            ValidateExtentParity: false),
        new(
            Name: "hawaii_infra_marine_sewerlines",
            ServiceUrl: "https://geodata.hawaii.gov/arcgis/rest/services/Infrastructure/MapServer",
            LayerId: 12,
            CompareField: "island",
            NumericField: "id",
            ValidateExtentParity: false),
        new(
            Name: "hawaii_historiccultural_moku",
            ServiceUrl: "https://geodata.hawaii.gov/arcgis/rest/services/HistoricCultural/MapServer",
            LayerId: 3,
            CompareField: "moku",
            NumericField: "gisacres",
            ValidateExtentParity: false),
        new(
            Name: "kauai_bridges",
            ServiceUrl: "https://maps.kauai.gov/server/rest/services/Bridges_with_condition_where_available/FeatureServer",
            LayerId: 2,
            CompareField: "NBI_Condition_Rating",
            NumericField: "Length",
            DateField: "last_edited_date"),
        new(
            Name: "esri_usa_highways",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/USA/MapServer",
            LayerId: 1,
            CompareField: "type",
            NumericField: "length"),
        new(
            Name: "esri_census_states",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Census/MapServer",
            LayerId: 3,
            CompareField: "STATE_NAME",
            NumericField: "POP2000",
            // Includes anti-meridian geometry around Alaska/Hawaii; extent parity is not stable in this projection path.
            ValidateExtentParity: false),
        new(
            Name: "esri_wildfire_lines",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Wildfire/FeatureServer",
            LayerId: 1,
            CompareField: "description",
            NumericField: "Shape__Length",
            DateField: "last_edited_date",
            ValidateExtentParity: false),
        new(
            Name: "esri_wildfire_polygons",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Wildfire/FeatureServer",
            LayerId: 2,
            CompareField: "description",
            NumericField: "Shape__Area",
            DateField: "last_edited_date",
            ValidateExtentParity: false),
        new(
            Name: "esri_military_ops_line_zm",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Military/FeatureServer",
            LayerId: 4,
            CompareField: "uniquedesignation",
            NumericField: "Shape__Length",
            DateField: "datetimevalid"),
        new(
            Name: "esri_military_ops_area_z",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Military/FeatureServer",
            LayerId: 5,
            CompareField: "uniquedesignation",
            NumericField: "Shape__Area",
            DateField: "datetimevalid"),
        new(
            Name: "esri_usa_cities",
            ServiceUrl: "https://sampleserver6.arcgisonline.com/arcgis/rest/services/USA/MapServer",
            LayerId: 0,
            CompareField: "areaname",
            NumericField: "pop2000")
    ];

    private readonly WebAppFixture _fixture = new();
    private readonly HttpClient _sourceClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly List<string> _importedTables = [];
    private readonly List<ParityScorecardEntry> _scorecardEntries = [];

    private HttpClient _adminClient = null!;
    private string _schema = string.Empty;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateAdminClient();
        _schema = _fixture.CurrentSchema ?? await _fixture.CreateIsolatedSchemaAsync(nameof(GeoservicesParityIntegrationTests));
    }

    public async Task DisposeAsync()
    {
        await WriteScorecardArtifactAsync();
        await CleanupImportedTablesAsync();
        await _fixture.DisposeAsync();
    }

    public void Dispose()
    {
        _sourceClient.Dispose();
    }

    [ExternalServiceTest(ExternalServicesEnv)]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task Import_FromCandidateServices_PreservesImportedTableParity()
    {
        foreach (var serviceCase in _serviceCases)
        {
            var sourceSnapshot = await CaptureSourceSnapshotAsync(serviceCase);

            var tableName = $"parity_{serviceCase.Name}_{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
            _importedTables.Add(tableName);

            var jobId = await StartImportAsync(serviceCase, tableName);
            var progress = await WaitForCompletionAsync(jobId, TimeSpan.FromMinutes(3));

            ReadStatus(progress.GetProperty("status")).Should().Be(
                GeoservicesImportStatus.Completed,
                because: $"import job {jobId} should complete for {serviceCase.Name}");
            progress.GetProperty("featuresProcessed").GetInt32().Should().Be(
                sourceSnapshot.TotalCount,
                because: "imported feature count should match source returnCountOnly");

            var tableSchema = await ResolveImportedTableSchemaAsync(tableName);
            var importedRowCount = await ReadImportedTableCountAsync(tableSchema, tableName);
            importedRowCount.Should().Be(sourceSnapshot.TotalCount);

            var importedColumns = await ReadImportedTableColumnsAsync(tableSchema, tableName);
            importedColumns.Should().Contain("fid", because: "import creates an internal primary key column");
            importedColumns.Should().Contain("geom", because: "imported feature layers should include geometry");

            foreach (var expectedField in sourceSnapshot.ExpectedImportedFields)
            {
                importedColumns.Should().Contain(
                    expectedField,
                    because: $"imported schema should include source field '{expectedField}'");
            }

            var importedCompareStats = await ReadImportedStringStatsAsync(
                tableSchema,
                tableName,
                serviceCase.CompareField.SanitizeFieldName());
            importedCompareStats.TotalCount.Should().Be(sourceSnapshot.TotalCount);
            importedCompareStats.NullCount.Should().Be(sourceSnapshot.CompareStats.NullCount);

            var importedNumericStats = await ReadImportedNumericStatsAsync(
                tableSchema,
                tableName,
                serviceCase.NumericField.SanitizeFieldName());
            importedNumericStats.TotalCount.Should().Be(sourceSnapshot.TotalCount);
            importedNumericStats.NullCount.Should().Be(sourceSnapshot.NumericStats.NullCount);
            importedNumericStats.Min.Should().NotBeNull();
            importedNumericStats.Max.Should().NotBeNull();
            sourceSnapshot.NumericStats.Min.Should().NotBeNull();
            sourceSnapshot.NumericStats.Max.Should().NotBeNull();
            importedNumericStats.Min!.Value.Should().BeApproximately(sourceSnapshot.NumericStats.Min!.Value, 1e-9);
            importedNumericStats.Max!.Value.Should().BeApproximately(sourceSnapshot.NumericStats.Max!.Value, 1e-9);

            if (serviceCase.DateField is { } sourceDateField)
            {
                var importedDateStats = await ReadImportedDateStatsAsync(
                    tableSchema,
                    tableName,
                    sourceDateField.SanitizeFieldName());

                importedDateStats.TotalCount.Should().Be(sourceSnapshot.TotalCount);
                importedDateStats.NullCount.Should().Be(sourceSnapshot.DateStats!.NullCount);
                importedDateStats.MinEpochMs.Should().NotBeNull();
                importedDateStats.MaxEpochMs.Should().NotBeNull();
                sourceSnapshot.DateStats.MinEpochMs.Should().NotBeNull();
                sourceSnapshot.DateStats.MaxEpochMs.Should().NotBeNull();
                Math.Abs(importedDateStats.MinEpochMs!.Value - sourceSnapshot.DateStats.MinEpochMs!.Value)
                    .Should().BeLessThanOrEqualTo(1);
                Math.Abs(importedDateStats.MaxEpochMs!.Value - sourceSnapshot.DateStats.MaxEpochMs!.Value)
                    .Should().BeLessThanOrEqualTo(1);
            }

            if (serviceCase.ValidateExtentParity)
            {
                var importedExtent = await ReadImportedExtent4326Async(tableSchema, tableName);
                importedExtent.Should().NotBeNull("imported rows should include geometry");
                sourceSnapshot.Extent4326.Should().NotBeNull("source extent query should succeed");
                importedExtent!.XMin.Should().BeApproximately(sourceSnapshot.Extent4326!.XMin, 0.01);
                importedExtent.YMin.Should().BeApproximately(sourceSnapshot.Extent4326.YMin, 0.01);
                importedExtent.XMax.Should().BeApproximately(sourceSnapshot.Extent4326.XMax, 0.01);
                importedExtent.YMax.Should().BeApproximately(sourceSnapshot.Extent4326.YMax, 0.01);
            }

            var sanitizedCompareField = serviceCase.CompareField.SanitizeFieldName();
            var importedSampleValues = await ReadImportedSampleValuesAsync(
                tableSchema,
                tableName,
                sanitizedCompareField,
                sourceSnapshot.SampleValues.Count);

            importedSampleValues.OrderBy(static value => value, StringComparer.Ordinal).Should().Equal(
                sourceSnapshot.SampleValues.OrderBy(static value => value, StringComparer.Ordinal),
                because: $"sample values for '{serviceCase.CompareField}' should remain stable after import");
        }
    }

    [ExternalServiceTest(ExternalServicesEnv)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task ImportAndPublish_FromCandidateServices_PreservesFeatureServerQueryParity()
    {
        var testConnectionId = await _fixture.GetTestSecureConnectionIdAsync();
        testConnectionId.Should().NotBeNull("web fixture should provision a reusable test secure connection");

        foreach (var serviceCase in _serviceCases)
        {
            var sourceSnapshot = await CaptureSourceSnapshotAsync(serviceCase);

            var tableName = $"parity_q_{serviceCase.Name}_{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
            _importedTables.Add(tableName);

            var jobId = await StartImportAsync(serviceCase, tableName);
            var progress = await WaitForCompletionAsync(jobId, TimeSpan.FromMinutes(3));

            ReadStatus(progress.GetProperty("status")).Should().Be(
                GeoservicesImportStatus.Completed,
                because: $"import job {jobId} should complete for {serviceCase.Name}");
            progress.GetProperty("featuresProcessed").GetInt32().Should().Be(sourceSnapshot.TotalCount);

            var tableSchema = await ResolveImportedTableSchemaAsync(tableName);
            var publishedLayer = await PublishImportedTableAsync(
                testConnectionId!.Value,
                tableSchema,
                tableName,
                serviceCase.Name);
            await ValidatePublishedLayerMetadataParityAsync(sourceSnapshot, publishedLayer);
            var seededRows = await SeedLayerFeaturesViaAppendAsync(
                publishedLayer.ServiceName,
                publishedLayer.LayerId,
                tableSchema,
                tableName);
            seededRows.Should().Be(sourceSnapshot.TotalCount);

            var sourceQueryEndpoint = $"{serviceCase.ServiceUrl.TrimEnd('/')}/{serviceCase.LayerId}/query";
            var honuaQueryEndpoint = $"/rest/services/{publishedLayer.ServiceName}/FeatureServer/{publishedLayer.LayerId}/query";
            var sanitizedCompareField = serviceCase.CompareField.SanitizeFieldName();
            var sanitizedNumericField = serviceCase.NumericField.SanitizeFieldName();
            var sanitizedDateField = serviceCase.DateField?.SanitizeFieldName();

            var honuaCount = await ReadCountQueryAsync(_adminClient, honuaQueryEndpoint);
            honuaCount.Should().Be(sourceSnapshot.TotalCount);

            var honuaRows = await QuerySourceRowsAsync(
                _adminClient,
                honuaQueryEndpoint,
                "objectid",
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);
            honuaRows.Count.Should().Be(sourceSnapshot.TotalCount);

            var sourceSignatures = BuildSemanticSignatures(
                sourceSnapshot.Rows,
                serviceCase.CompareField,
                serviceCase.NumericField,
                serviceCase.DateField);
            var honuaSignatures = BuildSemanticSignatures(
                honuaRows,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);
            honuaSignatures.Should().Equal(sourceSignatures);

            await ValidateAllFieldQueryParityAsync(
                sourceSnapshot,
                sourceQueryEndpoint,
                honuaQueryEndpoint);

            await ValidateEdgeCaseQueryParityAsync(
                sourceSnapshot,
                sourceQueryEndpoint,
                honuaQueryEndpoint,
                serviceCase,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);

            await ValidateHonuaMapServerQueryParityAsync(
                publishedLayer.ServiceName,
                publishedLayer.LayerId,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);

            await ValidateGeometryParityAsync(
                sourceQueryEndpoint,
                sourceSnapshot.ObjectIdField,
                honuaQueryEndpoint,
                serviceCase,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);

            var sourcePage = await QueryRowsPageAsync(
                _sourceClient,
                sourceQueryEndpoint,
                sourceSnapshot.ObjectIdField,
                BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
                0,
                QueryMatrixPageSize,
                returnGeometry: false);
            var honuaPage = await QueryRowsPageAsync(
                _adminClient,
                honuaQueryEndpoint,
                "objectid",
                BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
                0,
                QueryMatrixPageSize,
                returnGeometry: false);

            sourcePage.HasGeometry.Should().BeFalse("source query should honor returnGeometry=false");
            honuaPage.HasGeometry.Should().BeFalse("Honua query should honor returnGeometry=false");
            honuaPage.Rows.Count.Should().Be(sourcePage.Rows.Count);
            BuildSemanticSignatures(
                honuaPage.Rows,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField)
                .Should()
                .Equal(BuildSemanticSignatures(
                    sourcePage.Rows,
                    serviceCase.CompareField,
                    serviceCase.NumericField,
                    serviceCase.DateField));

            if (sourceSnapshot.TotalCount > QueryMatrixPageSize)
            {
                var sourceSecondPage = await QueryRowsPageAsync(
                    _sourceClient,
                    sourceQueryEndpoint,
                    sourceSnapshot.ObjectIdField,
                    BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
                    QueryMatrixPageSize,
                    QueryMatrixPageSize,
                    returnGeometry: false);
                var honuaSecondPage = await QueryRowsPageAsync(
                    _adminClient,
                    honuaQueryEndpoint,
                    "objectid",
                    BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
                    QueryMatrixPageSize,
                    QueryMatrixPageSize,
                    returnGeometry: false);

                honuaSecondPage.Rows.Count.Should().Be(sourceSecondPage.Rows.Count);
                BuildSemanticSignatures(
                    honuaSecondPage.Rows,
                    sanitizedCompareField,
                    sanitizedNumericField,
                    sanitizedDateField)
                    .Should()
                    .Equal(BuildSemanticSignatures(
                        sourceSecondPage.Rows,
                        serviceCase.CompareField,
                        serviceCase.NumericField,
                        serviceCase.DateField));
            }

            if (sourceSnapshot.TotalCount > QueryMatrixPageSize * 2)
            {
                var sourceThirdPage = await QueryRowsPageAsync(
                    _sourceClient,
                    sourceQueryEndpoint,
                    sourceSnapshot.ObjectIdField,
                    BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
                    QueryMatrixPageSize * 2,
                    QueryMatrixPageSize,
                    returnGeometry: false);
                var honuaThirdPage = await QueryRowsPageAsync(
                    _adminClient,
                    honuaQueryEndpoint,
                    "objectid",
                    BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
                    QueryMatrixPageSize * 2,
                    QueryMatrixPageSize,
                    returnGeometry: false);

                honuaThirdPage.Rows.Count.Should().Be(sourceThirdPage.Rows.Count);
                BuildSemanticSignatures(
                    honuaThirdPage.Rows,
                    sanitizedCompareField,
                    sanitizedNumericField,
                    sanitizedDateField)
                    .Should()
                    .Equal(BuildSemanticSignatures(
                        sourceThirdPage.Rows,
                        serviceCase.CompareField,
                        serviceCase.NumericField,
                        serviceCase.DateField));
            }

            await RecordScorecardForServiceAsync(
                serviceCase,
                sourceSnapshot,
                publishedLayer,
                sourceQueryEndpoint,
                honuaQueryEndpoint,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);
        }
    }

    private async Task<SourceLayerSnapshot> CaptureSourceSnapshotAsync(ParityServiceCase serviceCase)
    {
        var layerMetadata = await GetJsonElementAsync(
            _sourceClient,
            $"{serviceCase.ServiceUrl.TrimEnd('/')}/{serviceCase.LayerId}?f=pjson");

        var objectIdField = ResolveObjectIdField(layerMetadata);
        objectIdField.Should().NotBeNullOrWhiteSpace(
            because: $"source layer metadata should expose an object id field for {serviceCase.Name}");

        var comparableFieldMappings = ExtractComparableFieldMappings(layerMetadata);
        var expectedFields = comparableFieldMappings
            .Select(static field => field.SanitizedName)
            .ToHashSet(StringComparer.Ordinal);
        expectedFields.Should().Contain(
            serviceCase.CompareField.SanitizeFieldName(),
            because: $"source compare field '{serviceCase.CompareField}' should be present");

        var sourceCount = await ReadCountQueryAsync(
            _sourceClient,
            $"{serviceCase.ServiceUrl.TrimEnd('/')}/{serviceCase.LayerId}/query");

        var sourceRows = await QuerySourceRowsAsync(
            _sourceClient,
            $"{serviceCase.ServiceUrl.TrimEnd('/')}/{serviceCase.LayerId}/query",
            objectIdField!,
            serviceCase.CompareField,
            serviceCase.NumericField,
            serviceCase.DateField);

        sourceRows.Count.Should().Be(sourceCount);

        var compareStats = BuildStringStats(sourceRows, serviceCase.CompareField);
        var numericStats = BuildNumericStats(sourceRows, serviceCase.NumericField);
        var dateStats = serviceCase.DateField is null
            ? null
            : BuildDateStats(sourceRows, serviceCase.DateField);
        var extent4326 = await ReadSourceExtent4326Async(
            _sourceClient,
            $"{serviceCase.ServiceUrl.TrimEnd('/')}/{serviceCase.LayerId}/query");
        var sampleValues = sourceRows
            .Take(SampleFeatureCount)
            .Select(row => row.TryGetValue(serviceCase.CompareField, out var value)
                ? NormalizeValue(value)
                : "<null>")
            .ToArray();
        var sourceGeometryType = ReadOptionalString(layerMetadata, "geometryType");
        var supportsTimeQuery =
            TryGetPropertyCaseInsensitive(layerMetadata, "timeInfo", out var timeInfo) &&
            timeInfo.ValueKind == JsonValueKind.Object;
        var supportedQueryFormats = ReadOptionalString(layerMetadata, "supportedQueryFormats");
        var supportsGeoJson = !string.IsNullOrWhiteSpace(supportedQueryFormats) &&
            supportedQueryFormats.Contains("geojson", StringComparison.OrdinalIgnoreCase);

        return new SourceLayerSnapshot(
            ObjectIdField: objectIdField!,
            TotalCount: sourceCount,
            GeometryType: sourceGeometryType,
            SupportsTimeQuery: supportsTimeQuery,
            SupportsGeoJsonQuery: supportsGeoJson,
            ComparableFields: comparableFieldMappings,
            ExpectedImportedFields: expectedFields,
            CompareStats: compareStats,
            NumericStats: numericStats,
            DateStats: dateStats,
            Extent4326: extent4326,
            Rows: sourceRows,
            SampleValues: sampleValues);
    }

    private async Task<string> StartImportAsync(ParityServiceCase serviceCase, string tableName)
    {
        var startRequest = new
        {
            ServiceUrl = serviceCase.ServiceUrl,
            LayerId = serviceCase.LayerId,
            TableName = tableName,
            OverwriteExisting = true,
            BatchSize = 200,
            RequestTimeoutSeconds = 90,
            MaxRetries = 1,
            AutoPublish = false
        };

        using var startResponse = await _adminClient.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", startRequest);
        var startPayload = await ReadJsonPayloadAsync(startResponse);

        startResponse.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            because: $"import should queue for service {serviceCase.Name}. Payload: {startPayload}");

        using var startDocument = JsonDocument.Parse(startPayload);
        var jobId = startDocument.RootElement.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        return jobId!;
    }

    private async Task<JsonElement> WaitForCompletionAsync(string jobId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await _adminClient.GetAsync($"/api/v1/admin/import/geoservices/jobs/{jobId}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            var payload = await ReadJsonPayloadAsync(response);
            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                because: $"job status fetch should succeed for {jobId}. Payload: {payload}");

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement.Clone();
            var status = ReadStatus(root.GetProperty("status"));

            if (status is GeoservicesImportStatus.Completed or GeoservicesImportStatus.Failed or GeoservicesImportStatus.Cancelled)
            {
                return root;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"Geoservices import job {jobId} did not complete within {timeout.TotalSeconds} seconds.");
    }

    private async Task<string> ResolveImportedTableSchemaAsync(string tableName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_schema
            FROM information_schema.tables
            WHERE table_name = @tableName
              AND (table_schema = @preferredSchema OR table_schema = 'public')
            ORDER BY CASE WHEN table_schema = @preferredSchema THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("preferredSchema", _schema);

        var result = await command.ExecuteScalarAsync();
        result.Should().NotBeNull(
            because: $"imported table {tableName} should exist in schema '{_schema}' or 'public'");

        return Convert.ToString(result, CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException($"Unable to resolve schema for imported table {tableName}.");
    }

    private async Task<PublishedLayerHandle> PublishImportedTableAsync(
        Guid connectionId,
        string tableSchema,
        string tableName,
        string caseName)
    {
        var importedColumns = await ReadImportedTableColumnsAsync(tableSchema, tableName);
        importedColumns.Should().Contain("fid");
        importedColumns.Should().Contain("geom");

        var selectedFields = importedColumns
            .Where(static column => !string.Equals(column, "geom", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static column => column, StringComparer.Ordinal)
            .ToArray();

        var serviceName = $"parity_{caseName}_{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var publishRequest = new
        {
            Schema = tableSchema,
            Table = tableName,
            LayerName = $"Parity {caseName}",
            Description = "External query parity validation layer",
            GeometryColumn = "geom",
            PrimaryKey = "fid",
            Fields = selectedFields,
            ServiceName = serviceName,
            Enabled = true
        };

        using var publishResponse = await _adminClient.PostAsJsonAsync(
            $"/api/v1/admin/connections/{connectionId}/layers",
            publishRequest);
        var publishPayload = await ReadJsonPayloadAsync(publishResponse);

        publishResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            because: $"layer publish should succeed for imported table {tableSchema}.{tableName}. Payload: {publishPayload}");

        using var document = JsonDocument.Parse(publishPayload);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue("publish API should return success=true");
        var data = root.GetProperty("data");

        var layerId = data.GetProperty("layerId").GetInt32();
        var publishedServiceName = ReadOptionalString(data, "serviceName");
        publishedServiceName.Should().NotBeNullOrWhiteSpace();

        return new PublishedLayerHandle(layerId, publishedServiceName!);
    }

    private async Task<int> SeedLayerFeaturesViaAppendAsync(
        string serviceName,
        int layerId,
        string sourceTableSchema,
        string sourceTableName)
    {
        var appended = 0;
        var offset = 0;
        const int batchSize = 100;

        while (true)
        {
            var batch = await ReadImportedRowsForAppendAsync(
                sourceTableSchema,
                sourceTableName,
                offset,
                batchSize);

            if (batch.Rows.Count == 0)
            {
                break;
            }

            var edits = batch.Rows
                .Select(static row => new Dictionary<string, object?>
                {
                    ["attributes"] = row.Attributes,
                    ["geometry"] = row.Geometry
                })
                .ToList();

            var payload = new
            {
                edits = JsonSerializer.Serialize(edits),
                sourceFormat = "json",
                f = "json"
            };

            using var response = await _adminClient.PostAsJsonAsync(
                $"/rest/services/{serviceName}/FeatureServer/{layerId}/append",
                payload);
            var body = await ReadJsonPayloadAsync(response);

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                because: $"append should seed query parity layer data. Payload: {body}");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeTrue($"append should succeed. Payload: {body}");
            root.GetProperty("numFeaturesFailed").GetInt32().Should().Be(0);
            var appendedCount = root.GetProperty("numFeaturesAppended").GetInt32();
            appended += appendedCount;
            appendedCount.Should().Be(batch.Rows.Count);

            offset += batch.Rows.Count;
        }

        return appended;
    }

    private async Task<AppendBatchRows> ReadImportedRowsForAppendAsync(
        string schema,
        string tableName,
        int offset,
        int batchSize)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                (to_jsonb(src) - 'fid' - 'geom')::text AS attrs_json,
                CASE
                    WHEN src.geom IS NULL THEN NULL
                    WHEN ST_SRID(src.geom) = 4326 THEN ST_AsGeoJSON(src.geom)
                    WHEN ST_SRID(src.geom) = 0 THEN ST_AsGeoJSON(ST_SetSRID(src.geom, 4326))
                    ELSE ST_AsGeoJSON(ST_Transform(src.geom, 4326))
                END AS geom_geojson
            FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)} AS src
            ORDER BY src.fid
            LIMIT @batchSize
            OFFSET @offset;
            """;
        command.Parameters.AddWithValue("batchSize", batchSize);
        command.Parameters.AddWithValue("offset", offset);

        var rows = new List<AppendFeatureRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var attributesJson = reader.GetString(0);
            var attributes = ParseAttributesJson(attributesJson);
            var geoJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            var geometry = string.IsNullOrWhiteSpace(geoJson)
                ? null
                : ConvertGeoJsonToGeoServicesGeometry(geoJson);
            rows.Add(new AppendFeatureRow(attributes, geometry));
        }

        return new AppendBatchRows(rows);
    }

    private async Task ValidateEdgeCaseQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string honuaQueryEndpoint,
        ParityServiceCase serviceCase,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        var sourceObjectIds = await ReadObjectIdsAsync(_sourceClient, sourceQueryEndpoint);
        var honuaObjectIds = await ReadObjectIdsAsync(_adminClient, honuaQueryEndpoint);
        sourceObjectIds.Length.Should().BeGreaterThan(0);
        sourceObjectIds.Length.Should().BeLessThanOrEqualTo(sourceSnapshot.TotalCount);
        honuaObjectIds.Length.Should().Be(sourceObjectIds.Length);

        await ValidateReturnIdsOnlyQueryParityAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            serviceCase.CompareField,
            honuaQueryEndpoint,
            sanitizedCompareField);

        var sourceSubsetObjectIds = sourceObjectIds.Take(Math.Min(10, sourceObjectIds.Length)).ToArray();
        var honuaSubsetObjectIds = honuaObjectIds.Take(Math.Min(10, honuaObjectIds.Length)).ToArray();

        if (sourceSubsetObjectIds.Length > 0)
        {
            var sourceSubsetRows = await QueryRowsByObjectIdsAsync(
                _sourceClient,
                sourceQueryEndpoint,
                sourceSnapshot.ObjectIdField,
                serviceCase.CompareField,
                serviceCase.NumericField,
                serviceCase.DateField,
                sourceSubsetObjectIds);
            sourceSubsetRows.Count.Should().Be(sourceSubsetObjectIds.Length);
        }

        if (honuaSubsetObjectIds.Length > 0)
        {
            var honuaSubsetRows = await QueryRowsByObjectIdsAsync(
                _adminClient,
                honuaQueryEndpoint,
                "objectid",
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField,
                honuaSubsetObjectIds);
            honuaSubsetRows.Count.Should().Be(honuaSubsetObjectIds.Length);
        }

        var sourceOutFieldsWildcard = await QueryRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            BuildOutFields("*"),
            0,
            1,
            returnGeometry: false);
        var honuaOutFieldsWildcard = await QueryRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields("*"),
            0,
            1,
            returnGeometry: false);

        sourceOutFieldsWildcard.Rows.Count.Should().BeGreaterThan(0);
        honuaOutFieldsWildcard.Rows.Count.Should().BeGreaterThan(0);
        sourceOutFieldsWildcard.Rows[0].Should().ContainKey(serviceCase.CompareField);
        sourceOutFieldsWildcard.Rows[0].Should().ContainKey(serviceCase.NumericField);
        sourceOutFieldsWildcard.Rows[0].Should().ContainKey("objectid");
        honuaOutFieldsWildcard.Rows[0].Should().ContainKey(sanitizedCompareField);
        honuaOutFieldsWildcard.Rows[0].Should().ContainKey(sanitizedNumericField);
        honuaOutFieldsWildcard.Rows[0].Should().ContainKey("objectid");

        var sourceSingleOutField = await QueryRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            BuildOutFields(serviceCase.CompareField),
            0,
            1,
            returnGeometry: false);
        var honuaSingleOutField = await QueryRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields(sanitizedCompareField),
            0,
            1,
            returnGeometry: false);

        sourceSingleOutField.Rows.Count.Should().BeGreaterThan(0);
        honuaSingleOutField.Rows.Count.Should().BeGreaterThan(0);
        sourceSingleOutField.Rows[0].Should().ContainKey(serviceCase.CompareField);
        sourceSingleOutField.Rows[0].Should().NotContainKey(serviceCase.NumericField);
        honuaSingleOutField.Rows[0].Should().ContainKey(sanitizedCompareField);
        honuaSingleOutField.Rows[0].Should().NotContainKey(sanitizedNumericField);

        var sourceNotNullWhereCount = await QueryCountWithWhereAsync(
            _sourceClient,
            sourceQueryEndpoint,
            $"{serviceCase.CompareField} IS NOT NULL");
        var honuaNotNullWhereCount = await QueryCountWithWhereAsync(
            _adminClient,
            honuaQueryEndpoint,
            $"{sanitizedCompareField} IS NOT NULL");
        honuaNotNullWhereCount.Should().Be(sourceNotNullWhereCount);

        if (TryBuildFieldEqualityWhereClause(sourceSnapshot.Rows, serviceCase.CompareField, out var sourceWhereClause))
        {
            var sourceWhereCount = await QueryCountWithWhereAsync(_sourceClient, sourceQueryEndpoint, sourceWhereClause);
            var honuaWhereClause = sourceWhereClause.Replace(
                serviceCase.CompareField,
                sanitizedCompareField,
                StringComparison.Ordinal);
            var honuaWhereCount = await QueryCountWithWhereAsync(_adminClient, honuaQueryEndpoint, honuaWhereClause);
            honuaWhereCount.Should().Be(sourceWhereCount);
        }

        if (!string.IsNullOrWhiteSpace(serviceCase.DateField) && !string.IsNullOrWhiteSpace(sanitizedDateField))
        {
            var sourceDateNullCount = await QueryCountWithWhereAsync(
                _sourceClient,
                sourceQueryEndpoint,
                $"{serviceCase.DateField} IS NULL");
            var honuaDateNullCount = await QueryCountWithWhereAsync(
                _adminClient,
                honuaQueryEndpoint,
                $"{sanitizedDateField} IS NULL");
            honuaDateNullCount.Should().Be(sourceDateNullCount);

            var sourceDateNotNullCount = await QueryCountWithWhereAsync(
                _sourceClient,
                sourceQueryEndpoint,
                $"{serviceCase.DateField} IS NOT NULL");
            var honuaDateNotNullCount = await QueryCountWithWhereAsync(
                _adminClient,
                honuaQueryEndpoint,
                $"{sanitizedDateField} IS NOT NULL");
            honuaDateNotNullCount.Should().Be(sourceDateNotNullCount);
        }

        await ValidateNoMatchQueryParityAsync(
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            serviceCase.CompareField,
            honuaQueryEndpoint,
            sanitizedCompareField);

        await ValidateErrorParityAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            honuaQueryEndpoint);

        await ValidateStatisticsQueryParityAsync(
            sourceQueryEndpoint,
            serviceCase.NumericField,
            honuaQueryEndpoint,
            sanitizedNumericField);

        await ValidateGroupedStatisticsParityAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            serviceCase.CompareField,
            honuaQueryEndpoint,
            sanitizedCompareField);

        if (serviceCase.ValidateExtentParity)
        {
            await ValidateReturnExtentOnlyQueryParityAsync(sourceQueryEndpoint, honuaQueryEndpoint);
        }

        await ValidateTemporalTimeQueryParityAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            serviceCase.DateField,
            honuaQueryEndpoint,
            sanitizedDateField);

        await ValidateOrderByQueryParityAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            honuaQueryEndpoint,
            serviceCase,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField);

        if (sourceSnapshot.SupportsGeoJsonQuery)
        {
            await ValidateGeoJsonQueryParityAsync(
                sourceSnapshot,
                sourceQueryEndpoint,
                serviceCase,
                honuaQueryEndpoint,
                sanitizedCompareField,
                sanitizedNumericField,
                sanitizedDateField);
        }

        await ValidateDistinctQueryParityAsync(
            sourceQueryEndpoint,
            serviceCase.CompareField,
            honuaQueryEndpoint,
            sanitizedCompareField);

        await ValidateSpatialEnvelopeQueryParityAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            honuaQueryEndpoint);
    }

    private async Task ValidateNoMatchQueryParityAsync(
        string sourceQueryEndpoint,
        string sourceObjectIdField,
        string sourceCompareField,
        string honuaQueryEndpoint,
        string honuaCompareField)
    {
        const string noMatchSentinel = "__HONUA_PARITY_NO_MATCH_SENTINEL__";
        var sourceWhere = $"{sourceCompareField} = '{noMatchSentinel}'";
        var honuaWhere = $"{honuaCompareField} = '{noMatchSentinel}'";

        var sourceCount = await QueryCountWithWhereAsync(_sourceClient, sourceQueryEndpoint, sourceWhere);
        var honuaCount = await QueryCountWithWhereAsync(_adminClient, honuaQueryEndpoint, honuaWhere);
        sourceCount.Should().Be(0);
        honuaCount.Should().Be(0);

        var sourcePage = await QueryRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceObjectIdField,
            BuildOutFields(sourceCompareField),
            0,
            10,
            returnGeometry: false,
            whereClause: sourceWhere);
        var honuaPage = await QueryRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields(honuaCompareField),
            0,
            10,
            returnGeometry: false,
            whereClause: honuaWhere);

        sourcePage.Rows.Should().BeEmpty();
        honuaPage.Rows.Should().BeEmpty();
    }

    private async Task ValidateReturnIdsOnlyQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string sourceCompareField,
        string honuaQueryEndpoint,
        string honuaCompareField)
    {
        var sourceIds = await ReadObjectIdsAsync(_sourceClient, sourceQueryEndpoint);
        var honuaIds = await ReadObjectIdsAsync(_adminClient, honuaQueryEndpoint);

        sourceIds.Length.Should().BeGreaterThan(0);
        sourceIds.Length.Should().BeLessThanOrEqualTo(sourceSnapshot.TotalCount);
        honuaIds.Length.Should().Be(sourceIds.Length);
        sourceIds.Distinct().Count().Should().Be(sourceIds.Length);
        honuaIds.Distinct().Count().Should().Be(honuaIds.Length);

        if (!TryBuildFieldInWhereClause(sourceSnapshot.Rows, sourceCompareField, 25, out var sourceWhere))
        {
            return;
        }

        var honuaWhere = sourceWhere.Replace(
            sourceCompareField,
            honuaCompareField,
            StringComparison.Ordinal);

        var sourceFilteredIds = await ReadObjectIdsAsync(_sourceClient, sourceQueryEndpoint, sourceWhere);
        var honuaFilteredIds = await ReadObjectIdsAsync(_adminClient, honuaQueryEndpoint, honuaWhere);
        var sourceFilteredCount = await QueryCountWithWhereAsync(_sourceClient, sourceQueryEndpoint, sourceWhere);
        var honuaFilteredCount = await QueryCountWithWhereAsync(_adminClient, honuaQueryEndpoint, honuaWhere);

        sourceFilteredIds.Length.Should().BeLessThanOrEqualTo(sourceFilteredCount);
        honuaFilteredIds.Length.Should().BeLessThanOrEqualTo(honuaFilteredCount);
        honuaFilteredIds.Length.Should().Be(sourceFilteredIds.Length);
    }

    private async Task ValidateStatisticsQueryParityAsync(
        string sourceQueryEndpoint,
        string sourceNumericField,
        string honuaQueryEndpoint,
        string honuaNumericField)
    {
        var sourceWhere = $"{sourceNumericField} IS NOT NULL";
        var honuaWhere = $"{honuaNumericField} IS NOT NULL";
        var sourceOutStatistics = BuildNumericStatisticsQuery(sourceNumericField);
        var honuaOutStatistics = BuildNumericStatisticsQuery(honuaNumericField);

        var sourceStats = await QueryStatisticsAttributesAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceWhere,
            sourceOutStatistics);
        var honuaStats = await QueryStatisticsAttributesAsync(
            _adminClient,
            honuaQueryEndpoint,
            honuaWhere,
            honuaOutStatistics);

        var sourceCount = ReadRequiredNumericStatistic(sourceStats, "parity_count");
        var honuaCount = ReadRequiredNumericStatistic(honuaStats, "parity_count");
        honuaCount.Should().Be(sourceCount);

        var sourceMin = ReadRequiredNumericStatistic(sourceStats, "parity_min");
        var honuaMin = ReadRequiredNumericStatistic(honuaStats, "parity_min");
        honuaMin.Should().BeApproximately(sourceMin, 1e-3);

        var sourceMax = ReadRequiredNumericStatistic(sourceStats, "parity_max");
        var honuaMax = ReadRequiredNumericStatistic(honuaStats, "parity_max");
        honuaMax.Should().BeApproximately(sourceMax, 1e-3);
    }

    private async Task ValidateGroupedStatisticsParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string sourceGroupField,
        string honuaQueryEndpoint,
        string honuaGroupField)
    {
        if (!TryBuildFieldInWhereClause(sourceSnapshot.Rows, sourceGroupField, 20, out var sourceWhere))
        {
            return;
        }

        var honuaWhere = sourceWhere.Replace(
            sourceGroupField,
            honuaGroupField,
            StringComparison.Ordinal);

        var sourceGroups = await QueryGroupedCountStatisticsAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceGroupField,
            sourceWhere);
        var honuaGroups = await QueryGroupedCountStatisticsAsync(
            _adminClient,
            honuaQueryEndpoint,
            honuaGroupField,
            honuaWhere);

        honuaGroups.Should().Equal(sourceGroups);
    }

    private async Task ValidateTemporalTimeQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string? sourceDateField,
        string honuaQueryEndpoint,
        string? honuaDateField)
    {
        if (!sourceSnapshot.SupportsTimeQuery ||
            string.IsNullOrWhiteSpace(sourceDateField) ||
            string.IsNullOrWhiteSpace(honuaDateField) ||
            sourceSnapshot.DateStats?.MinEpochMs is null ||
            sourceSnapshot.DateStats.MaxEpochMs is null)
        {
            return;
        }

        var minEpoch = sourceSnapshot.DateStats.MinEpochMs.Value;
        var maxEpoch = sourceSnapshot.DateStats.MaxEpochMs.Value;
        if (maxEpoch <= minEpoch)
        {
            return;
        }

        var midpoint = minEpoch + ((maxEpoch - minEpoch) / 2);
        var timeExtent = $"{minEpoch.ToString(CultureInfo.InvariantCulture)},{midpoint.ToString(CultureInfo.InvariantCulture)}";

        var sourceTimeCount = await QueryCountWithTimeAsync(_sourceClient, sourceQueryEndpoint, timeExtent);
        var honuaTimeCount = await QueryCountWithTimeAsync(_adminClient, honuaQueryEndpoint, timeExtent);
        honuaTimeCount.Should().Be(sourceTimeCount);
    }

    private async Task ValidateGeoJsonQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        ParityServiceCase serviceCase,
        string honuaQueryEndpoint,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        var sourceGeoJsonPage = await QueryGeoJsonRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
            0,
            25,
            returnGeometry: true);
        var honuaGeoJsonPage = await QueryGeoJsonRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
            0,
            25,
            returnGeometry: true);

        honuaGeoJsonPage.Rows.Count.Should().Be(sourceGeoJsonPage.Rows.Count);
        honuaGeoJsonPage.HasGeometry.Should().Be(sourceGeoJsonPage.HasGeometry);
        BuildTolerantSemanticSignatures(
            honuaGeoJsonPage.Rows,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField,
            numericDecimals: 6)
            .Should()
            .Equal(BuildTolerantSemanticSignatures(
                sourceGeoJsonPage.Rows,
                serviceCase.CompareField,
                serviceCase.NumericField,
                serviceCase.DateField,
                numericDecimals: 6));

        var sourceGeoJsonNoGeomPage = await QueryGeoJsonRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
            0,
            10,
            returnGeometry: false);
        var honuaGeoJsonNoGeomPage = await QueryGeoJsonRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
            0,
            10,
            returnGeometry: false);

        sourceGeoJsonNoGeomPage.HasGeometry.Should().BeFalse();
        honuaGeoJsonNoGeomPage.HasGeometry.Should().BeFalse();
    }

    private async Task ValidateErrorParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string honuaQueryEndpoint)
    {
        var malformedOutStatistics = Uri.EscapeDataString("[{\"statisticType\":\"count\"");
        var cases = new List<ParityErrorCase>
        {
            new(
                Name: "invalid_where",
                SourceRequestUri: $"{sourceQueryEndpoint}?where={Uri.EscapeDataString("1 =")}&outFields=*&f=json",
                HonuaRequestUri: $"{honuaQueryEndpoint}?where={Uri.EscapeDataString("1 =")}&outFields=*&f=json"),
            new(
                Name: "malformed_outstatistics",
                SourceRequestUri: $"{sourceQueryEndpoint}?where=1%3D1&outStatistics={malformedOutStatistics}&f=json",
                HonuaRequestUri: $"{honuaQueryEndpoint}?where=1%3D1&outStatistics={malformedOutStatistics}&f=json")
        };

        if (sourceSnapshot.SupportsTimeQuery)
        {
            cases.Add(new ParityErrorCase(
                Name: "invalid_time",
                SourceRequestUri: $"{sourceQueryEndpoint}?where=1%3D1&time=not-a-time&returnCountOnly=true&f=json",
                HonuaRequestUri: $"{honuaQueryEndpoint}?where=1%3D1&time=not-a-time&returnCountOnly=true&f=json"));
        }

        foreach (var parityCase in cases)
        {
            var sourceError = await QueryErrorSignatureAsync(_sourceClient, parityCase.SourceRequestUri);
            var honuaError = await QueryErrorSignatureAsync(_adminClient, parityCase.HonuaRequestUri);

            if (!sourceError.IsError)
            {
                continue;
            }

            honuaError.IsError.Should().BeTrue(
                because: $"Honua should return an error when source does for '{parityCase.Name}'");
            honuaError.ErrorFamily.Should().Be(
                sourceError.ErrorFamily,
                because: $"error class should match source for '{parityCase.Name}'");
            honuaError.NormalizedCode.Should().NotBeNull(
                because: $"Honua should return an error code for '{parityCase.Name}'");
        }
    }

    private async Task ValidateHonuaMapServerQueryParityAsync(
        string serviceName,
        int layerId,
        string compareField,
        string numericField,
        string? dateField)
    {
        var featureQueryEndpoint = $"/rest/services/{serviceName}/FeatureServer/{layerId}/query";
        var mapQueryEndpoint = $"/rest/services/{serviceName}/MapServer/{layerId}/query";

        var featureCount = await ReadCountQueryAsync(_adminClient, featureQueryEndpoint);
        var mapCount = await ReadCountQueryAsync(_adminClient, mapQueryEndpoint);
        mapCount.Should().Be(featureCount);

        var featurePage = await QueryRowsPageAsync(
            _adminClient,
            featureQueryEndpoint,
            "objectid",
            BuildOutFields(compareField, numericField, dateField),
            0,
            50,
            returnGeometry: false);
        var mapPage = await QueryRowsPageAsync(
            _adminClient,
            mapQueryEndpoint,
            "objectid",
            BuildOutFields(compareField, numericField, dateField),
            0,
            50,
            returnGeometry: false);

        mapPage.Rows.Count.Should().Be(featurePage.Rows.Count);
        mapPage.HasGeometry.Should().BeFalse();
        featurePage.HasGeometry.Should().BeFalse();
        BuildSemanticSignatures(mapPage.Rows, compareField, numericField, dateField)
            .Should()
            .Equal(BuildSemanticSignatures(featurePage.Rows, compareField, numericField, dateField));
    }

    private async Task ValidateReturnExtentOnlyQueryParityAsync(
        string sourceQueryEndpoint,
        string honuaQueryEndpoint)
    {
        var sourceExtent = await ReadSourceExtent4326Async(_sourceClient, sourceQueryEndpoint);
        var honuaExtent = await ReadSourceExtent4326Async(_adminClient, honuaQueryEndpoint);

        sourceExtent.Should().NotBeNull();
        honuaExtent.Should().NotBeNull();

        honuaExtent!.XMin.Should().BeApproximately(sourceExtent!.XMin, 0.01);
        honuaExtent.YMin.Should().BeApproximately(sourceExtent.YMin, 0.01);
        honuaExtent.XMax.Should().BeApproximately(sourceExtent.XMax, 0.01);
        honuaExtent.YMax.Should().BeApproximately(sourceExtent.YMax, 0.01);
    }

    private async Task ValidateAllFieldQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string honuaQueryEndpoint)
    {
        var sourceRows = await QueryAllRowsWithAllFieldsAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField);
        var honuaRows = await QueryAllRowsWithAllFieldsAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid");

        sourceRows.Count.Should().Be(sourceSnapshot.TotalCount);
        honuaRows.Count.Should().Be(sourceSnapshot.TotalCount);

        var sourceSignatures = BuildAllFieldSignatures(
            sourceRows,
            sourceSnapshot.ComparableFields,
            useSanitizedFieldNames: false);
        var honuaSignatures = BuildAllFieldSignatures(
            honuaRows,
            sourceSnapshot.ComparableFields,
            useSanitizedFieldNames: true);

        honuaSignatures.Should().Equal(sourceSignatures);
    }

    private async Task ValidatePublishedLayerMetadataParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        PublishedLayerHandle publishedLayer)
    {
        var honuaLayerMetadata = await GetJsonElementAsync(
            _adminClient,
            $"/rest/services/{publishedLayer.ServiceName}/FeatureServer/{publishedLayer.LayerId}?f=pjson");

        if (!string.IsNullOrWhiteSpace(sourceSnapshot.GeometryType))
        {
            var honuaGeometryType = ReadOptionalString(honuaLayerMetadata, "geometryType");
            honuaGeometryType.Should().Be(
                sourceSnapshot.GeometryType,
                because: "published layer should preserve source geometry type");
        }

        var honuaFields = ExtractComparableFieldMappings(honuaLayerMetadata)
            .Select(static field => field.SanitizedName)
            .ToHashSet(StringComparer.Ordinal);
        honuaFields.Should().Contain(
            sourceSnapshot.ExpectedImportedFields,
            because: "published layer should expose imported source fields");

        var unexpectedHonuaFields = honuaFields
            .Except(sourceSnapshot.ExpectedImportedFields, StringComparer.Ordinal)
            .ToArray();
        unexpectedHonuaFields.Should().OnlyContain(
            static field => string.Equals(field, "fid", StringComparison.Ordinal),
            because: "published metadata should only add the table primary key field beyond imported source fields");
    }

    private async Task ValidateOrderByQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string honuaQueryEndpoint,
        ParityServiceCase serviceCase,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        var sourceWhere = $"{serviceCase.NumericField} IS NOT NULL AND {serviceCase.CompareField} IS NOT NULL";
        var honuaWhere = $"{sanitizedNumericField} IS NOT NULL AND {sanitizedCompareField} IS NOT NULL";

        if (!string.IsNullOrWhiteSpace(serviceCase.DateField) && !string.IsNullOrWhiteSpace(sanitizedDateField))
        {
            sourceWhere = $"{sourceWhere} AND {serviceCase.DateField} IS NOT NULL";
            honuaWhere = $"{honuaWhere} AND {sanitizedDateField} IS NOT NULL";
        }

        var sourceFilteredCount = await QueryCountWithWhereAsync(_sourceClient, sourceQueryEndpoint, sourceWhere);
        var honuaFilteredCount = await QueryCountWithWhereAsync(_adminClient, honuaQueryEndpoint, honuaWhere);
        honuaFilteredCount.Should().Be(sourceFilteredCount);
        if (sourceFilteredCount == 0)
        {
            return;
        }

        var pageSize = Math.Min(25, sourceFilteredCount);
        var sourceOrderByAsc = BuildOrderByClause(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField);
        var honuaOrderByAsc = BuildOrderByClause(sanitizedCompareField, sanitizedNumericField, sanitizedDateField);
        var sourceOrderByDesc = BuildOrderByClause(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField, descending: true);
        var honuaOrderByDesc = BuildOrderByClause(sanitizedCompareField, sanitizedNumericField, sanitizedDateField, descending: true);

        var sourceAscRows = await QueryRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceOrderByAsc,
            BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
            0,
            pageSize,
            returnGeometry: false,
            whereClause: sourceWhere);
        var honuaAscRows = await QueryRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            honuaOrderByAsc,
            BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
            0,
            pageSize,
            returnGeometry: false,
            whereClause: honuaWhere);

        honuaAscRows.Rows.Count.Should().Be(sourceAscRows.Rows.Count);
        BuildSemanticSequence(
            honuaAscRows.Rows,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField)
            .Should()
            .Equal(BuildSemanticSequence(
                sourceAscRows.Rows,
                serviceCase.CompareField,
                serviceCase.NumericField,
                serviceCase.DateField));

        var sourceDescRows = await QueryRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceOrderByDesc,
            BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
            0,
            pageSize,
            returnGeometry: false,
            whereClause: sourceWhere);
        var honuaDescRows = await QueryRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            honuaOrderByDesc,
            BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
            0,
            pageSize,
            returnGeometry: false,
            whereClause: honuaWhere);

        honuaDescRows.Rows.Count.Should().Be(sourceDescRows.Rows.Count);
        BuildSemanticSequence(
            honuaDescRows.Rows,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField)
            .Should()
            .Equal(BuildSemanticSequence(
                sourceDescRows.Rows,
                serviceCase.CompareField,
                serviceCase.NumericField,
                serviceCase.DateField));
    }

    private async Task ValidateDistinctQueryParityAsync(
        string sourceQueryEndpoint,
        string sourceCompareField,
        string honuaQueryEndpoint,
        string honuaCompareField)
    {
        var sourceDistinctValues = await QueryDistinctValuesAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceCompareField);
        var honuaDistinctValues = await QueryDistinctValuesAsync(
            _adminClient,
            honuaQueryEndpoint,
            honuaCompareField);

        honuaDistinctValues.Should().Equal(sourceDistinctValues);
    }

    private async Task ValidateSpatialEnvelopeQueryParityAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        string honuaQueryEndpoint)
    {
        if (sourceSnapshot.Extent4326 is null)
        {
            return;
        }

        var extent = sourceSnapshot.Extent4326;
        var xSpan = extent.XMax - extent.XMin;
        var ySpan = extent.YMax - extent.YMin;
        if (xSpan <= 0 || ySpan <= 0)
        {
            return;
        }

        var xInset = xSpan * 0.25;
        var yInset = ySpan * 0.25;
        var envelope = new ExtentStats(
            XMin: extent.XMin + xInset,
            YMin: extent.YMin + yInset,
            XMax: extent.XMax - xInset,
            YMax: extent.YMax - yInset);

        if (envelope.XMin >= envelope.XMax || envelope.YMin >= envelope.YMax)
        {
            envelope = extent;
        }

        var sourceSpatialCount = await QueryCountWithEnvelopeAsync(_sourceClient, sourceQueryEndpoint, envelope);
        var honuaSpatialCount = await QueryCountWithEnvelopeAsync(_adminClient, honuaQueryEndpoint, envelope);
        Math.Abs(honuaSpatialCount - sourceSpatialCount).Should().BeLessThanOrEqualTo(
            1,
            because: "envelope intersects counts can differ by a single boundary feature after reprojection");
    }

    private async Task ValidateGeometryParityAsync(
        string sourceQueryEndpoint,
        string sourceOrderByField,
        string honuaQueryEndpoint,
        ParityServiceCase serviceCase,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        var sourceRowsWithGeometry = await QuerySourceRowsWithGeometryAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceOrderByField,
            serviceCase.CompareField,
            serviceCase.NumericField,
            serviceCase.DateField);
        var honuaRowsWithGeometry = await QuerySourceRowsWithGeometryAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField);

        var sourceGeometrySignatures = BuildGeometrySignatures(
            sourceRowsWithGeometry,
            serviceCase.CompareField,
            serviceCase.NumericField,
            serviceCase.DateField);
        var honuaGeometrySignatures = BuildGeometrySignatures(
            honuaRowsWithGeometry,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField);

        AssertGeometryParity(sourceGeometrySignatures, honuaGeometrySignatures, 0.0001);
    }

    private static async Task<JsonElement> GetJsonElementAsync(HttpClient client, string requestUri)
    {
        const int maxAttempts = 4;
        HttpStatusCode? lastStatusCode = null;
        string lastPayload = string.Empty;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(requestUri);
                var payload = await ReadJsonPayloadAsync(response);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var document = JsonDocument.Parse(payload);
                    return document.RootElement.Clone();
                }

                lastStatusCode = response.StatusCode;
                lastPayload = payload;

                if (!ShouldRetryStatusCode(response.StatusCode) || attempt == maxAttempts)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                if (attempt == maxAttempts)
                {
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
        }

        if (lastStatusCode.HasValue)
        {
            lastStatusCode.Value.Should().Be(
                HttpStatusCode.OK,
                because: $"GET {requestUri} should succeed. Payload: {lastPayload}");
        }

        throw new InvalidOperationException(
            $"GET {requestUri} failed after {maxAttempts} attempts.",
            lastException);
    }

    private static bool ShouldRetryStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static async Task<ErrorSignature> QueryErrorSignatureAsync(HttpClient client, string requestUri)
    {
        const int maxAttempts = 4;
        HttpStatusCode statusCode = default;
        string payload = string.Empty;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var response = await client.GetAsync(requestUri);
            statusCode = response.StatusCode;
            payload = await ReadJsonPayloadAsync(response);

            if (!ShouldRetryStatusCode(statusCode) || attempt == maxAttempts)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
        }

        int? normalizedCode = (int)statusCode >= 400
            ? (int)statusCode
            : null;
        var message = string.Empty;
        var hasErrorObject = false;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (TryGetPropertyCaseInsensitive(root, "error", out var errorObject) &&
                    errorObject.ValueKind == JsonValueKind.Object)
                {
                    hasErrorObject = true;
                    if (TryGetPropertyCaseInsensitive(errorObject, "code", out var code) &&
                        code.ValueKind == JsonValueKind.Number &&
                        code.TryGetInt32(out var errorCode))
                    {
                        normalizedCode = errorCode;
                    }

                    message = ReadOptionalString(errorObject, "message")
                              ?? ReadOptionalString(root, "message")
                              ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // Non-JSON error bodies are acceptable for this parity signature.
            }
        }

        var isError = hasErrorObject || (int)statusCode >= 400;
        var errorFamily = (normalizedCode ?? (int)statusCode) / 100;

        return new ErrorSignature(
            IsError: isError,
            StatusCode: statusCode,
            NormalizedCode: normalizedCode,
            ErrorFamily: errorFamily,
            Message: message);
    }

    private static async Task<int> ReadCountQueryAsync(
        HttpClient client,
        string queryEndpoint,
        string whereClause = "1=1")
    {
        var requestUri =
            $"{queryEndpoint}?where={Uri.EscapeDataString(whereClause)}&returnCountOnly=true&f=json";
        var json = await GetJsonElementAsync(client, requestUri);
        return json.GetProperty("count").GetInt32();
    }

    private static async Task<IReadOnlyList<Dictionary<string, JsonElement>>> QuerySourceRowsAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField,
        string compareField,
        string numericField,
        string? dateField)
    {
        var fields = new List<string> { orderByField, compareField, numericField };
        if (!string.IsNullOrWhiteSpace(dateField))
        {
            fields.Add(dateField);
        }

        var rows = new List<Dictionary<string, JsonElement>>();
        var offset = 0;

        while (true)
        {
            var page = await QueryRowsPageAsync(
                client,
                queryEndpoint,
                orderByField,
                fields,
                offset,
                200,
                returnGeometry: false);
            rows.AddRange(page.Rows);

            if (page.Rows.Count == 0 || !page.ExceededTransferLimit)
            {
                break;
            }

            offset += page.Rows.Count;
        }

        return rows;
    }

    private static async Task<IReadOnlyList<FeatureRowWithGeometry>> QuerySourceRowsWithGeometryAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField,
        string compareField,
        string numericField,
        string? dateField)
    {
        var fields = new List<string> { orderByField, compareField, numericField };
        if (!string.IsNullOrWhiteSpace(dateField))
        {
            fields.Add(dateField);
        }

        var rows = new List<FeatureRowWithGeometry>();
        var offset = 0;

        while (true)
        {
            var page = await QueryRowsPageAsync(
                client,
                queryEndpoint,
                orderByField,
                fields,
                offset,
                200,
                returnGeometry: true,
                outSrid: 4326);
            rows.AddRange(page.Features);

            if (page.Rows.Count == 0 || !page.ExceededTransferLimit)
            {
                break;
            }

            offset += page.Rows.Count;
        }

        return rows;
    }

    private static async Task<IReadOnlyList<Dictionary<string, JsonElement>>> QueryAllRowsWithAllFieldsAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField)
    {
        var rows = new List<Dictionary<string, JsonElement>>();
        var offset = 0;

        while (true)
        {
            var page = await QueryRowsPageAsync(
                client,
                queryEndpoint,
                orderByField,
                BuildOutFields("*"),
                offset,
                200,
                returnGeometry: false);
            rows.AddRange(page.Rows);

            if (page.Rows.Count == 0 || !page.ExceededTransferLimit)
            {
                break;
            }

            offset += page.Rows.Count;
        }

        return rows;
    }

    private static async Task<QueryPageResult> QueryRowsPageAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField,
        IReadOnlyList<string> outFields,
        int resultOffset,
        int resultRecordCount,
        bool returnGeometry,
        string whereClause = "1=1",
        string? objectIds = null,
        int? outSrid = null,
        bool returnDistinctValues = false)
    {
        var query = $"{queryEndpoint}?where={Uri.EscapeDataString(whereClause)}&outFields={Uri.EscapeDataString(string.Join(",", outFields))}" +
                    $"&returnGeometry={(returnGeometry ? "true" : "false")}" +
                    $"&orderByFields={Uri.EscapeDataString(orderByField)}" +
                    $"&resultOffset={resultOffset}&resultRecordCount={resultRecordCount}&f=json";

        if (!string.IsNullOrWhiteSpace(objectIds))
        {
            query += $"&objectIds={Uri.EscapeDataString(objectIds)}";
        }

        if (outSrid.HasValue)
        {
            query += $"&outSR={outSrid.Value}";
        }

        if (returnDistinctValues)
        {
            query += "&returnDistinctValues=true";
        }

        var json = await GetJsonElementAsync(client, query);
        var rows = ExtractRows(json);
        var featureRows = ExtractFeatureRows(json);
        var hasGeometry = QueryResultContainsGeometry(json);
        var exceededTransferLimit =
            TryGetPropertyCaseInsensitive(json, "exceededTransferLimit", out var exceededElement) &&
            exceededElement.ValueKind == JsonValueKind.True;

        return new QueryPageResult(rows, featureRows, hasGeometry, exceededTransferLimit);
    }

    private static async Task<long[]> ReadObjectIdsAsync(
        HttpClient client,
        string queryEndpoint,
        string whereClause = "1=1")
    {
        const int pageSize = 1000;
        var offset = 0;
        var ids = new List<long>();

        while (true)
        {
            var query =
                $"{queryEndpoint}?where={Uri.EscapeDataString(whereClause)}&returnIdsOnly=true&resultOffset={offset}&resultRecordCount={pageSize}&f=json";
            var json = await GetJsonElementAsync(client, query);

            var pageIds = TryGetPropertyCaseInsensitive(json, "objectIds", out var objectIds) &&
                          objectIds.ValueKind == JsonValueKind.Array
                ? objectIds
                    .EnumerateArray()
                    .Where(static value => value.ValueKind == JsonValueKind.Number)
                    .Select(static value => value.GetInt64())
                    .ToArray()
                : [];

            if (pageIds.Length == 0)
            {
                break;
            }

            ids.AddRange(pageIds);
            var exceededTransferLimit =
                TryGetPropertyCaseInsensitive(json, "exceededTransferLimit", out var exceededElement) &&
                exceededElement.ValueKind == JsonValueKind.True;
            if (!exceededTransferLimit)
            {
                break;
            }

            offset += pageIds.Length;
        }

        return ids.Distinct().ToArray();
    }

    private static async Task<int> QueryCountWithWhereAsync(
        HttpClient client,
        string queryEndpoint,
        string whereClause)
    {
        return await ReadCountQueryAsync(client, queryEndpoint, whereClause);
    }

    private static async Task<IReadOnlyList<Dictionary<string, JsonElement>>> QueryRowsByObjectIdsAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField,
        string compareField,
        string numericField,
        string? dateField,
        long[] objectIds)
    {
        var ids = string.Join(",", objectIds.Select(static id => id.ToString(CultureInfo.InvariantCulture)));
        var fields = BuildOutFields(compareField, numericField, dateField);
        var page = await QueryRowsPageAsync(
            client,
            queryEndpoint,
            orderByField,
            fields,
            0,
            objectIds.Length,
            returnGeometry: false,
            objectIds: ids);
        return page.Rows;
    }

    private static async Task<string[]> QueryDistinctValuesAsync(
        HttpClient client,
        string queryEndpoint,
        string fieldName)
    {
        var query =
            $"{queryEndpoint}?where={Uri.EscapeDataString($"{fieldName} IS NOT NULL")}" +
            $"&outFields={Uri.EscapeDataString(fieldName)}" +
            $"&orderByFields={Uri.EscapeDataString(fieldName)}" +
            "&resultOffset=0" +
            "&resultRecordCount=200" +
            "&returnDistinctValues=true" +
            "&returnGeometry=false" +
            "&f=json";
        var json = await GetJsonElementAsync(client, query);
        var rows = ExtractRows(json);

        var distinctValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.TryGetValue(fieldName, out var value))
            {
                distinctValues.Add(NormalizeValue(value));
            }
        }

        return distinctValues.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static async Task<int> QueryCountWithEnvelopeAsync(
        HttpClient client,
        string queryEndpoint,
        ExtentStats envelope)
    {
        var geometry = string.Join(
            ",",
            envelope.XMin.ToString("G17", CultureInfo.InvariantCulture),
            envelope.YMin.ToString("G17", CultureInfo.InvariantCulture),
            envelope.XMax.ToString("G17", CultureInfo.InvariantCulture),
            envelope.YMax.ToString("G17", CultureInfo.InvariantCulture));
        var requestUri = $"{queryEndpoint}?where=1%3D1" +
                         $"&geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryEnvelope" +
                         "&spatialRel=esriSpatialRelIntersects" +
                         "&inSR=4326" +
                         "&returnCountOnly=true&f=json";
        var json = await GetJsonElementAsync(client, requestUri);
        return json.GetProperty("count").GetInt32();
    }

    private static async Task<Dictionary<string, JsonElement>> QueryStatisticsAttributesAsync(
        HttpClient client,
        string queryEndpoint,
        string whereClause,
        string outStatistics)
    {
        var requestUri =
            $"{queryEndpoint}?where={Uri.EscapeDataString(whereClause)}" +
            $"&outStatistics={Uri.EscapeDataString(outStatistics)}" +
            "&returnGeometry=false&f=json";
        var json = await GetJsonElementAsync(client, requestUri);
        var rows = ExtractRows(json);
        rows.Should().ContainSingle(
            because: "aggregate statistics query should return a single summary row");
        return rows[0];
    }

    private static async Task<string[]> QueryGroupedCountStatisticsAsync(
        HttpClient client,
        string queryEndpoint,
        string groupField,
        string whereClause)
    {
        var outStatistics = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    statisticType = "count",
                    onStatisticField = groupField,
                    outStatisticFieldName = "parity_group_count"
                }
            });

        var requestUri =
            $"{queryEndpoint}?where={Uri.EscapeDataString(whereClause)}" +
            $"&groupByFieldsForStatistics={Uri.EscapeDataString(groupField)}" +
            $"&outStatistics={Uri.EscapeDataString(outStatistics)}" +
            $"&orderByFields={Uri.EscapeDataString(groupField)}" +
            "&resultOffset=0&resultRecordCount=100&returnGeometry=false&f=json";
        var json = await GetJsonElementAsync(client, requestUri);
        var rows = ExtractRows(json);

        var signatures = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var groupValue = row.TryGetValue(groupField, out var value)
                ? NormalizeValue(value)
                : "<missing>";
            var countValue = row.TryGetValue("parity_group_count", out var countElement) &&
                             TryGetNumericValue(countElement, out var countNumber)
                ? Convert.ToInt64(Math.Round(countNumber, 0, MidpointRounding.AwayFromZero), CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture)
                : "<null>";
            signatures.Add($"{groupValue}\u001f{countValue}");
        }

        signatures.Sort(StringComparer.Ordinal);
        return signatures.ToArray();
    }

    private static async Task<int> QueryCountWithTimeAsync(
        HttpClient client,
        string queryEndpoint,
        string timeExtent)
    {
        var requestUri =
            $"{queryEndpoint}?where=1%3D1&time={Uri.EscapeDataString(timeExtent)}&returnCountOnly=true&f=json";
        var json = await GetJsonElementAsync(client, requestUri);
        return json.GetProperty("count").GetInt32();
    }

    private static async Task<GeoJsonQueryPageResult> QueryGeoJsonRowsPageAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField,
        IReadOnlyList<string> outFields,
        int resultOffset,
        int resultRecordCount,
        bool returnGeometry,
        string whereClause = "1=1")
    {
        var requestUri = $"{queryEndpoint}?where={Uri.EscapeDataString(whereClause)}&outFields={Uri.EscapeDataString(string.Join(",", outFields))}" +
                         $"&returnGeometry={(returnGeometry ? "true" : "false")}" +
                         $"&orderByFields={Uri.EscapeDataString(orderByField)}" +
                         $"&resultOffset={resultOffset}&resultRecordCount={resultRecordCount}&f=geojson";
        var json = await GetJsonElementAsync(client, requestUri);

        var rows = new List<Dictionary<string, JsonElement>>();
        var hasGeometry = false;

        if (TryGetPropertyCaseInsensitive(json, "features", out var features) &&
            features.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in features.EnumerateArray())
            {
                if (!TryGetPropertyCaseInsensitive(feature, "properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in properties.EnumerateObject())
                {
                    row[property.Name] = property.Value.Clone();
                }

                rows.Add(row);

                if (!hasGeometry &&
                    TryGetPropertyCaseInsensitive(feature, "geometry", out var geometry) &&
                    geometry.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    hasGeometry = true;
                }
            }
        }

        return new GeoJsonQueryPageResult(rows, hasGeometry);
    }

    private static string BuildNumericStatisticsQuery(string numericField)
    {
        var definitions = new object[]
        {
            new
            {
                statisticType = "count",
                onStatisticField = numericField,
                outStatisticFieldName = "parity_count"
            },
            new
            {
                statisticType = "min",
                onStatisticField = numericField,
                outStatisticFieldName = "parity_min"
            },
            new
            {
                statisticType = "max",
                onStatisticField = numericField,
                outStatisticFieldName = "parity_max"
            }
        };

        return JsonSerializer.Serialize(definitions);
    }

    private static double ReadRequiredNumericStatistic(
        Dictionary<string, JsonElement> attributes,
        string fieldName)
    {
        attributes.TryGetValue(fieldName, out var value).Should().BeTrue(
            because: $"statistics response should include '{fieldName}'");
        TryGetNumericValue(value, out var numeric).Should().BeTrue(
            because: $"statistics field '{fieldName}' should be numeric");
        return numeric;
    }

    private static bool TryBuildFieldEqualityWhereClause(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string fieldName,
        out string whereClause)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var text = value.GetString();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    whereClause = $"{fieldName} = '{EscapeSqlLiteral(text)}'";
                    return true;
                }
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    whereClause = $"{fieldName} = {value.GetRawText()}";
                    return true;
            }
        }

        whereClause = string.Empty;
        return false;
    }

    private static bool TryBuildFieldInWhereClause(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string fieldName,
        int maxValues,
        out string whereClause)
    {
        var literals = new List<string>(maxValues);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            string? literal = value.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) =>
                    $"'{EscapeSqlLiteral(value.GetString()!)}'",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (string.IsNullOrWhiteSpace(literal) || !seen.Add(literal))
            {
                continue;
            }

            literals.Add(literal);
            if (literals.Count >= maxValues)
            {
                break;
            }
        }

        if (literals.Count == 0)
        {
            whereClause = string.Empty;
            return false;
        }

        whereClause = $"{fieldName} IN ({string.Join(",", literals)})";
        return true;
    }

    private static async Task<ExtentStats?> ReadSourceExtent4326Async(HttpClient client, string queryEndpoint)
    {
        var query = $"{queryEndpoint}?where=1%3D1&returnExtentOnly=true&outSR=4326&f=json";
        var json = await GetJsonElementAsync(client, query);

        if (!TryGetPropertyCaseInsensitive(json, "extent", out var extent) || extent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ExtentStats(
            XMin: extent.GetProperty("xmin").GetDouble(),
            YMin: extent.GetProperty("ymin").GetDouble(),
            XMax: extent.GetProperty("xmax").GetDouble(),
            YMax: extent.GetProperty("ymax").GetDouble());
    }

    private async Task<int> ReadImportedTableCountAsync(string schema, string tableName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)};";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task<HashSet<string>> ReadImportedTableColumnsAsync(string schema, string tableName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND table_name = @table
            ORDER BY ordinal_position;
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", tableName);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private async Task<IReadOnlyList<string>> ReadImportedSampleValuesAsync(
        string schema,
        string tableName,
        string compareField,
        int sampleCount)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COALESCE({QuoteIdentifier(compareField)}::text, '<null>') " +
            $"FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)} " +
            "ORDER BY fid LIMIT @sampleCount;";
        command.Parameters.AddWithValue("sampleCount", sampleCount);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task<StringFieldStats> ReadImportedStringStatsAsync(string schema, string tableName, string fieldName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*)::bigint, SUM(CASE WHEN {QuoteIdentifier(fieldName)} IS NULL THEN 1 ELSE 0 END)::bigint " +
            $"FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)};";

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new StringFieldStats(
            TotalCount: reader.GetInt64(0),
            NullCount: reader.GetInt64(1));
    }

    private async Task<NumericFieldStats> ReadImportedNumericStatsAsync(string schema, string tableName, string fieldName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*)::bigint, " +
            $"SUM(CASE WHEN {QuoteIdentifier(fieldName)} IS NULL THEN 1 ELSE 0 END)::bigint, " +
            $"MIN({QuoteIdentifier(fieldName)}::double precision), " +
            $"MAX({QuoteIdentifier(fieldName)}::double precision) " +
            $"FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)};";

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new NumericFieldStats(
            TotalCount: reader.GetInt64(0),
            NullCount: reader.GetInt64(1),
            Min: reader.IsDBNull(2) ? null : reader.GetDouble(2),
            Max: reader.IsDBNull(3) ? null : reader.GetDouble(3));
    }

    private async Task<DateFieldStats> ReadImportedDateStatsAsync(string schema, string tableName, string fieldName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*)::bigint, " +
            $"SUM(CASE WHEN {QuoteIdentifier(fieldName)} IS NULL THEN 1 ELSE 0 END)::bigint, " +
            $"CAST(FLOOR(EXTRACT(EPOCH FROM MIN({QuoteIdentifier(fieldName)}))*1000) AS bigint), " +
            $"CAST(FLOOR(EXTRACT(EPOCH FROM MAX({QuoteIdentifier(fieldName)}))*1000) AS bigint) " +
            $"FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)};";

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new DateFieldStats(
            TotalCount: reader.GetInt64(0),
            NullCount: reader.GetInt64(1),
            MinEpochMs: reader.IsDBNull(2) ? null : reader.GetInt64(2),
            MaxEpochMs: reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private async Task<ExtentStats?> ReadImportedExtent4326Async(string schema, string tableName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT " +
            $"MIN(ST_XMin(ST_Transform(geom, 4326))), " +
            $"MIN(ST_YMin(ST_Transform(geom, 4326))), " +
            $"MAX(ST_XMax(ST_Transform(geom, 4326))), " +
            $"MAX(ST_YMax(ST_Transform(geom, 4326))) " +
            $"FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)} " +
            "WHERE geom IS NOT NULL;";

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            return null;
        }

        return new ExtentStats(
            XMin: reader.GetDouble(0),
            YMin: reader.GetDouble(1),
            XMax: reader.GetDouble(2),
            YMax: reader.GetDouble(3));
    }

    private static StringFieldStats BuildStringStats(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string fieldName)
    {
        var nullCount = rows.Count(row =>
            !row.TryGetValue(fieldName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);

        return new StringFieldStats(rows.Count, nullCount);
    }

    private static NumericFieldStats BuildNumericStats(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string fieldName)
    {
        var values = new List<double>();
        var nullCount = 0;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                nullCount++;
                continue;
            }

            if (TryGetNumericValue(value, out var numeric))
            {
                values.Add(numeric);
            }
            else
            {
                nullCount++;
            }
        }

        return new NumericFieldStats(
            TotalCount: rows.Count,
            NullCount: nullCount,
            Min: values.Count > 0 ? values.Min() : null,
            Max: values.Count > 0 ? values.Max() : null);
    }

    private static DateFieldStats BuildDateStats(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string fieldName)
    {
        var values = new List<long>();
        var nullCount = 0;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                nullCount++;
                continue;
            }

            if (TryGetEpochMilliseconds(value, out var epochMs))
            {
                values.Add(epochMs);
            }
            else
            {
                nullCount++;
            }
        }

        return new DateFieldStats(
            TotalCount: rows.Count,
            NullCount: nullCount,
            MinEpochMs: values.Count > 0 ? values.Min() : null,
            MaxEpochMs: values.Count > 0 ? values.Max() : null);
    }

    private static bool TryGetNumericValue(JsonElement value, out double numeric)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out numeric))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out numeric))
        {
            return true;
        }

        numeric = default;
        return false;
    }

    private static bool TryGetEpochMilliseconds(JsonElement value, out long epochMs)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out epochMs))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out epochMs))
            {
                return true;
            }

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dateTimeOffset))
            {
                epochMs = dateTimeOffset.ToUnixTimeMilliseconds();
                return true;
            }
        }

        epochMs = default;
        return false;
    }

    private static List<FieldParityMapping> ExtractComparableFieldMappings(JsonElement metadata)
    {
        var comparableFields = new List<FieldParityMapping>();
        var sanitizedNames = new HashSet<string>(StringComparer.Ordinal);

        if (!TryGetPropertyCaseInsensitive(metadata, "fields", out var fieldArray) ||
            fieldArray.ValueKind != JsonValueKind.Array)
        {
            return comparableFields;
        }

        foreach (var field in fieldArray.EnumerateArray())
        {
            var fieldType = ReadOptionalString(field, "type");
            if (string.IsNullOrWhiteSpace(fieldType) || !IsComparableFieldType(fieldType))
            {
                continue;
            }

            var name = ReadOptionalString(field, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var sanitized = name.SanitizeFieldName();
                var added = sanitizedNames.Add(sanitized);
                added.Should().BeTrue(
                    because: $"source field names should map to unique sanitized names, but '{sanitized}' was duplicated");
                comparableFields.Add(new FieldParityMapping(name, sanitized, fieldType));
            }
        }

        return comparableFields;
    }

    private static bool IsComparableFieldType(string fieldType)
    {
        return fieldType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEOID" => false,
            "ESRIFIELDTYPEGEOMETRY" => false,
            "ESRIFIELDTYPEBLOB" => false,
            "ESRIFIELDTYPERASTER" => false,
            _ => true
        };
    }

    private static List<Dictionary<string, JsonElement>> ExtractRows(JsonElement queryResponse)
    {
        var rows = new List<Dictionary<string, JsonElement>>();

        if (!TryGetPropertyCaseInsensitive(queryResponse, "features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(feature, "attributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in attributes.EnumerateObject())
            {
                row[property.Name] = property.Value.Clone();
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<FeatureRowWithGeometry> ExtractFeatureRows(JsonElement queryResponse)
    {
        var rows = new List<FeatureRowWithGeometry>();

        if (!TryGetPropertyCaseInsensitive(queryResponse, "features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (!TryGetPropertyCaseInsensitive(feature, "attributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in attributes.EnumerateObject())
            {
                row[property.Name] = property.Value.Clone();
            }

            JsonElement? geometry = null;
            if (TryGetPropertyCaseInsensitive(feature, "geometry", out var geometryElement) &&
                geometryElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                geometry = geometryElement.Clone();
            }

            rows.Add(new FeatureRowWithGeometry(row, geometry));
        }

        return rows;
    }

    private static bool QueryResultContainsGeometry(JsonElement queryResponse)
    {
        if (!TryGetPropertyCaseInsensitive(queryResponse, "features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (TryGetPropertyCaseInsensitive(feature, "geometry", out var geometry) &&
                geometry.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, object?> ParseAttributesJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = ConvertJsonElementToObject(property.Value);
        }

        return result;
    }

    private static Dictionary<string, object?> ConvertGeoJsonToGeoServicesGeometry(string geoJson)
    {
        using var document = JsonDocument.Parse(geoJson);
        var root = document.RootElement;
        var geometryType = root.GetProperty("type").GetString();
        var coordinates = root.GetProperty("coordinates");

        return geometryType switch
        {
            "Point" => ConvertGeoJsonPoint(coordinates),
            "MultiPoint" => ConvertGeoJsonMultiPoint(coordinates),
            "LineString" => ConvertGeoJsonLineString(coordinates),
            "MultiLineString" => ConvertGeoJsonMultiLineString(coordinates),
            "Polygon" => ConvertGeoJsonPolygon(coordinates),
            "MultiPolygon" => ConvertGeoJsonMultiPolygon(coordinates),
            _ => throw new InvalidOperationException($"Unsupported GeoJSON geometry type '{geometryType}'.")
        };
    }

    private static Dictionary<string, object?> ConvertGeoJsonPoint(JsonElement coordinates)
    {
        var values = coordinates.EnumerateArray().ToArray();
        var geometry = new Dictionary<string, object?>
        {
            ["x"] = values[0].GetDouble(),
            ["y"] = values[1].GetDouble()
        };

        if (values.Length >= 3)
        {
            geometry["z"] = values[2].GetDouble();
            geometry["hasZ"] = true;
        }

        return geometry;
    }

    private static Dictionary<string, object?> ConvertGeoJsonMultiPoint(JsonElement coordinates)
    {
        var points = coordinates
            .EnumerateArray()
            .Select(static point => point.EnumerateArray().Select(static value => value.GetDouble()).ToArray())
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["points"] = points
        };
    }

    private static Dictionary<string, object?> ConvertGeoJsonLineString(JsonElement coordinates)
    {
        var path = coordinates
            .EnumerateArray()
            .Select(static point => point.EnumerateArray().Select(static value => value.GetDouble()).ToArray())
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["paths"] = new[] { path }
        };
    }

    private static Dictionary<string, object?> ConvertGeoJsonMultiLineString(JsonElement coordinates)
    {
        var paths = coordinates
            .EnumerateArray()
            .Select(static line =>
                line.EnumerateArray()
                    .Select(static point => point.EnumerateArray().Select(static value => value.GetDouble()).ToArray())
                    .ToArray())
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["paths"] = paths
        };
    }

    private static Dictionary<string, object?> ConvertGeoJsonPolygon(JsonElement coordinates)
    {
        var rings = coordinates
            .EnumerateArray()
            .Select(static ring =>
                ring.EnumerateArray()
                    .Select(static point => point.EnumerateArray().Select(static value => value.GetDouble()).ToArray())
                    .ToArray())
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["rings"] = rings
        };
    }

    private static Dictionary<string, object?> ConvertGeoJsonMultiPolygon(JsonElement coordinates)
    {
        var rings = new List<double[][]>();
        foreach (var polygon in coordinates.EnumerateArray())
        {
            foreach (var ring in polygon.EnumerateArray())
            {
                rings.Add(
                    ring.EnumerateArray()
                        .Select(static point => point.EnumerateArray().Select(static value => value.GetDouble()).ToArray())
                        .ToArray());
            }
        }

        return new Dictionary<string, object?>
        {
            ["rings"] = rings.ToArray()
        };
    }

    private static object? ConvertJsonElementToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ConvertNumericJsonElement(value),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonElementToObject).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ConvertJsonElementToObject(property.Value),
                StringComparer.OrdinalIgnoreCase),
            _ => value.GetRawText()
        };
    }

    private static object ConvertNumericJsonElement(JsonElement value)
    {
        if (value.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        return value.GetDouble();
    }

    private static List<string> BuildSemanticSignatures(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string compareField,
        string numericField,
        string? dateField)
    {
        var signatures = new List<string>(rows.Count);

        foreach (var row in rows)
        {
            var compareValue = row.TryGetValue(compareField, out var compareElement)
                ? NormalizeValue(compareElement)
                : "<null>";

            var numericValue = row.TryGetValue(numericField, out var numericElement) &&
                               TryGetNumericValue(numericElement, out var numeric)
                ? numeric.ToString("G17", CultureInfo.InvariantCulture)
                : "<null>";

            var signature = $"{compareValue}\u001f{numericValue}";
            if (!string.IsNullOrWhiteSpace(dateField))
            {
                var dateValue = row.TryGetValue(dateField, out var dateElement) &&
                                TryGetEpochMilliseconds(dateElement, out var epochMs)
                    ? epochMs.ToString(CultureInfo.InvariantCulture)
                    : "<null>";

                signature = $"{signature}\u001f{dateValue}";
            }

            signatures.Add(signature);
        }

        signatures.Sort(StringComparer.Ordinal);
        return signatures;
    }

    private static List<string> BuildSemanticSequence(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string compareField,
        string numericField,
        string? dateField)
    {
        var signatures = new List<string>(rows.Count);

        foreach (var row in rows)
        {
            signatures.Add(BuildSemanticSignature(row, compareField, numericField, dateField));
        }

        return signatures;
    }

    private static List<string> BuildTolerantSemanticSignatures(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        string compareField,
        string numericField,
        string? dateField,
        int numericDecimals)
    {
        var signatures = new List<string>(rows.Count);

        foreach (var row in rows)
        {
            var compareValue = row.TryGetValue(compareField, out var compareElement)
                ? NormalizeValue(compareElement)
                : "<null>";
            var numericValue = row.TryGetValue(numericField, out var numericElement) &&
                               TryGetNumericValue(numericElement, out var numeric)
                ? Math.Round(numeric, numericDecimals, MidpointRounding.AwayFromZero)
                    .ToString($"F{numericDecimals}", CultureInfo.InvariantCulture)
                : "<null>";

            var signature = $"{compareValue}\u001f{numericValue}";
            if (!string.IsNullOrWhiteSpace(dateField))
            {
                var dateValue = row.TryGetValue(dateField, out var dateElement) &&
                                TryGetEpochMilliseconds(dateElement, out var epochMs)
                    ? epochMs.ToString(CultureInfo.InvariantCulture)
                    : "<null>";
                signature = $"{signature}\u001f{dateValue}";
            }

            signatures.Add(signature);
        }

        signatures.Sort(StringComparer.Ordinal);
        return signatures;
    }

    private static List<string> BuildAllFieldSignatures(
        IReadOnlyList<Dictionary<string, JsonElement>> rows,
        IReadOnlyList<FieldParityMapping> comparableFields,
        bool useSanitizedFieldNames)
    {
        var signatures = new List<string>(rows.Count);
        var orderedFields = comparableFields
            .OrderBy(static field => field.SanitizedName, StringComparer.Ordinal)
            .ToArray();

        foreach (var row in rows)
        {
            var builder = new StringBuilder();
            foreach (var field in orderedFields)
            {
                var queryFieldName = useSanitizedFieldNames ? field.SanitizedName : field.SourceName;
                var normalizedValue = row.TryGetValue(queryFieldName, out var value)
                    ? NormalizeComparableFieldValue(value, field.SourceType)
                    : "<missing>";
                builder
                    .Append(field.SanitizedName)
                    .Append('=')
                    .Append(normalizedValue)
                    .Append('\u001f');
            }

            signatures.Add(builder.ToString());
        }

        signatures.Sort(StringComparer.Ordinal);
        return signatures;
    }

    private static List<GeometrySignatureEntry> BuildGeometrySignatures(
        IReadOnlyList<FeatureRowWithGeometry> rows,
        string compareField,
        string numericField,
        string? dateField)
    {
        var signatures = new List<GeometrySignatureEntry>(rows.Count);

        foreach (var row in rows)
        {
            var semantic = BuildSemanticSignature(row.Attributes, compareField, numericField, dateField);
            var geometrySignature = BuildGeometrySummary(row.Geometry);
            signatures.Add(new GeometrySignatureEntry(
                semantic,
                geometrySignature.Kind,
                geometrySignature.MinX,
                geometrySignature.MinY,
                geometrySignature.MaxX,
                geometrySignature.MaxY,
                geometrySignature.VertexCount,
                geometrySignature.Hash));
        }

        signatures.Sort(static (left, right) => CompareGeometryEntries(left, right));
        return signatures;
    }

    private static string BuildSemanticSignature(
        IReadOnlyDictionary<string, JsonElement> row,
        string compareField,
        string numericField,
        string? dateField)
    {
        var compareValue = row.TryGetValue(compareField, out var compareElement)
            ? NormalizeValue(compareElement)
            : "<null>";

        var numericValue = row.TryGetValue(numericField, out var numericElement) &&
                           TryGetNumericValue(numericElement, out var numeric)
            ? numeric.ToString("G17", CultureInfo.InvariantCulture)
            : "<null>";

        var signature = $"{compareValue}\u001f{numericValue}";
        if (string.IsNullOrWhiteSpace(dateField))
        {
            return signature;
        }

        var dateValue = row.TryGetValue(dateField, out var dateElement) &&
                        TryGetEpochMilliseconds(dateElement, out var epochMs)
            ? epochMs.ToString(CultureInfo.InvariantCulture)
            : "<null>";

        return $"{signature}\u001f{dateValue}";
    }

    private static GeometrySummary BuildGeometrySummary(JsonElement? geometryElement)
    {
        if (geometryElement is null)
        {
            return new GeometrySummary("null", 0, 0, 0, 0, 0, "null");
        }

        var geometry = geometryElement.Value;
        var coordinateTokens = new List<string>();
        ExtractGeometryCoordinates(geometry, coordinateTokens);
        if (coordinateTokens.Count == 0)
        {
            return new GeometrySummary("empty", 0, 0, 0, 0, 0, "empty");
        }

        var envelope = ComputeEnvelope(coordinateTokens);
        var kind = DetermineGeometryKind(geometry);
        var coarseEnvelope = FormattableString.Invariant(
            $"{Math.Round(envelope.MinX, 4):F4},{Math.Round(envelope.MinY, 4):F4},{Math.Round(envelope.MaxX, 4):F4},{Math.Round(envelope.MaxY, 4):F4}");
        var hashInput = $"{kind}:{coarseEnvelope}:{coordinateTokens.Count}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));

        return new GeometrySummary(
            kind,
            envelope.MinX,
            envelope.MinY,
            envelope.MaxX,
            envelope.MaxY,
            coordinateTokens.Count,
            hash[..16]);
    }

    private static void ExtractGeometryCoordinates(JsonElement geometry, List<string> tokens)
    {
        if (geometry.TryGetProperty("x", out var xElement) &&
            geometry.TryGetProperty("y", out var yElement) &&
            xElement.ValueKind == JsonValueKind.Number &&
            yElement.ValueKind == JsonValueKind.Number)
        {
            tokens.Add(FormatCoordinateToken(xElement.GetDouble(), yElement.GetDouble()));
            return;
        }

        if (geometry.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            ExtractCoordinateArray(points, tokens);
            return;
        }

        if (geometry.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            foreach (var path in paths.EnumerateArray())
            {
                ExtractCoordinateArray(path, tokens);
            }

            return;
        }

        if (geometry.TryGetProperty("rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
        {
            foreach (var ring in rings.EnumerateArray())
            {
                ExtractCoordinateArray(ring, tokens);
            }
        }
    }

    private static void ExtractCoordinateArray(JsonElement points, List<string> tokens)
    {
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = point.EnumerateArray().ToArray();
            if (values.Length < 2 ||
                values[0].ValueKind != JsonValueKind.Number ||
                values[1].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            tokens.Add(FormatCoordinateToken(values[0].GetDouble(), values[1].GetDouble()));
        }
    }

    private static string FormatCoordinateToken(double x, double y)
    {
        return FormattableString.Invariant(
            $"{Math.Round(x, 5):F5},{Math.Round(y, 5):F5}");
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) ComputeEnvelope(
        IReadOnlyList<string> coordinateTokens)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var token in coordinateTokens)
        {
            var parts = token.Split(',', 2, StringSplitOptions.None);
            var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var y = double.Parse(parts[1], CultureInfo.InvariantCulture);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return (minX, minY, maxX, maxY);
    }

    private static string DetermineGeometryKind(JsonElement geometry)
    {
        if (geometry.TryGetProperty("x", out _))
        {
            return "point";
        }

        if (geometry.TryGetProperty("points", out _))
        {
            return "multipoint";
        }

        if (geometry.TryGetProperty("paths", out _))
        {
            return "polyline";
        }

        if (geometry.TryGetProperty("rings", out _))
        {
            return "polygon";
        }

        return "unknown";
    }

    private static void AssertGeometryParity(
        IReadOnlyList<GeometrySignatureEntry> expected,
        IReadOnlyList<GeometrySignatureEntry> actual,
        double envelopeTolerance)
    {
        actual.Count.Should().Be(expected.Count);

        var expectedOrdered = expected
            .OrderBy(static item => item.Semantic, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ThenBy(static item => item.VertexCount)
            .ThenBy(static item => item.MinX)
            .ThenBy(static item => item.MinY)
            .ThenBy(static item => item.MaxX)
            .ThenBy(static item => item.MaxY)
            .ToArray();
        var actualOrdered = actual
            .OrderBy(static item => item.Semantic, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ThenBy(static item => item.VertexCount)
            .ThenBy(static item => item.MinX)
            .ThenBy(static item => item.MinY)
            .ThenBy(static item => item.MaxX)
            .ThenBy(static item => item.MaxY)
            .ToArray();

        for (var index = 0; index < expectedOrdered.Length; index++)
        {
            var expectedEntry = expectedOrdered[index];
            var actualEntry = actualOrdered[index];

            actualEntry.Semantic.Should().Be(expectedEntry.Semantic);
            actualEntry.Kind.Should().Be(expectedEntry.Kind);
            actualEntry.VertexCount.Should().Be(expectedEntry.VertexCount);
            actualEntry.MinX.Should().BeApproximately(expectedEntry.MinX, envelopeTolerance);
            actualEntry.MinY.Should().BeApproximately(expectedEntry.MinY, envelopeTolerance);
            actualEntry.MaxX.Should().BeApproximately(expectedEntry.MaxX, envelopeTolerance);
            actualEntry.MaxY.Should().BeApproximately(expectedEntry.MaxY, envelopeTolerance);
            actualEntry.Hash.Should().Be(expectedEntry.Hash);
        }
    }

    private static int CompareGeometryEntries(GeometrySignatureEntry left, GeometrySignatureEntry right)
    {
        var semantic = string.Compare(left.Semantic, right.Semantic, StringComparison.Ordinal);
        if (semantic != 0)
        {
            return semantic;
        }

        var kind = string.Compare(left.Kind, right.Kind, StringComparison.Ordinal);
        if (kind != 0)
        {
            return kind;
        }

        var vertices = left.VertexCount.CompareTo(right.VertexCount);
        if (vertices != 0)
        {
            return vertices;
        }

        var minX = left.MinX.CompareTo(right.MinX);
        if (minX != 0)
        {
            return minX;
        }

        var minY = left.MinY.CompareTo(right.MinY);
        if (minY != 0)
        {
            return minY;
        }

        var maxX = left.MaxX.CompareTo(right.MaxX);
        if (maxX != 0)
        {
            return maxX;
        }

        var maxY = left.MaxY.CompareTo(right.MaxY);
        if (maxY != 0)
        {
            return maxY;
        }

        return string.Compare(left.Hash, right.Hash, StringComparison.Ordinal);
    }

    private static string[] BuildOutFields(params string?[] fields)
    {
        return fields
            .Where(static field => !string.IsNullOrWhiteSpace(field))
            .Select(static field => field!)
            .ToArray();
    }

    private static string BuildOrderByClause(
        string compareField,
        string numericField,
        string? dateField,
        bool descending = false)
    {
        var direction = descending ? "DESC" : "ASC";
        var fields = new List<string>
        {
            $"{numericField} {direction}",
            $"{compareField} {direction}"
        };

        if (!string.IsNullOrWhiteSpace(dateField))
        {
            fields.Add($"{dateField} {direction}");
        }

        return string.Join(",", fields);
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string? ResolveObjectIdField(JsonElement metadata)
    {
        var explicitField = ReadOptionalString(metadata, "objectIdField");
        if (!string.IsNullOrWhiteSpace(explicitField))
        {
            return explicitField;
        }

        if (TryGetPropertyCaseInsensitive(metadata, "fields", out var fieldArray) &&
            fieldArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldArray.EnumerateArray())
            {
                var fieldType = ReadOptionalString(field, "type");
                if (!string.Equals(fieldType, "esriFieldTypeOID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = ReadOptionalString(field, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static string NormalizeValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "<null>",
            JsonValueKind.String => value.GetString() ?? "<null>",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private static string NormalizeComparableFieldValue(JsonElement value, string sourceFieldType)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "<null>";
        }

        return sourceFieldType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEDATE" when TryGetEpochMilliseconds(value, out var epochMs) => epochMs.ToString(CultureInfo.InvariantCulture),
            "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" or "ESRIFIELDTYPEOID" when TryGetInt64Value(value, out var intValue) => intValue.ToString(CultureInfo.InvariantCulture),
            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" when TryGetNumericValue(value, out var numericValue) => numericValue.ToString("G17", CultureInfo.InvariantCulture),
            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" => NormalizeGuidValue(value),
            _ => NormalizeValue(value)
        };
    }

    private static string NormalizeGuidValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return NormalizeValue(value);
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<null>";
        }

        var normalized = text.Trim().Trim('{', '}').ToLowerInvariant();
        return normalized;
    }

    private static bool TryGetInt64Value(JsonElement value, out long intValue)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out intValue))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            return true;
        }

        intValue = default;
        return false;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        return TryGetPropertyCaseInsensitive(element, propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private async Task<ParityLatencyMetrics> CaptureLatencyMetricsAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        ParityServiceCase serviceCase,
        string honuaQueryEndpoint,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        var sourceLatencies = await MeasureQueryLatenciesAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField));
        var honuaLatencies = await MeasureQueryLatenciesAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField));

        var sourceP50 = ComputePercentile(sourceLatencies, 0.50);
        var sourceP95 = ComputePercentile(sourceLatencies, 0.95);
        var sourceP99 = ComputePercentile(sourceLatencies, 0.99);
        var honuaP50 = ComputePercentile(honuaLatencies, 0.50);
        var honuaP95 = ComputePercentile(honuaLatencies, 0.95);
        var honuaP99 = ComputePercentile(honuaLatencies, 0.99);

        return new ParityLatencyMetrics(
            SampleCount: sourceLatencies.Length,
            SourceP50Ms: sourceP50,
            SourceP95Ms: sourceP95,
            SourceP99Ms: sourceP99,
            HonuaP50Ms: honuaP50,
            HonuaP95Ms: honuaP95,
            HonuaP99Ms: honuaP99,
            HonuaToSourceP95Ratio: sourceP95 > 0 ? honuaP95 / sourceP95 : null,
            HonuaToSourceP99Ratio: sourceP99 > 0 ? honuaP99 / sourceP99 : null);
    }

    private async Task<ParityTransferLimitMetrics> CaptureTransferLimitMetricsAsync(
        SourceLayerSnapshot sourceSnapshot,
        string sourceQueryEndpoint,
        ParityServiceCase serviceCase,
        string honuaQueryEndpoint,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        const int pageSize = 25;

        var sourcePage = await QueryRowsPageAsync(
            _sourceClient,
            sourceQueryEndpoint,
            sourceSnapshot.ObjectIdField,
            BuildOutFields(serviceCase.CompareField, serviceCase.NumericField, serviceCase.DateField),
            0,
            pageSize,
            returnGeometry: false);
        var honuaPage = await QueryRowsPageAsync(
            _adminClient,
            honuaQueryEndpoint,
            "objectid",
            BuildOutFields(sanitizedCompareField, sanitizedNumericField, sanitizedDateField),
            0,
            pageSize,
            returnGeometry: false);

        return new ParityTransferLimitMetrics(
            PageSize: pageSize,
            SourceExceededTransferLimit: sourcePage.ExceededTransferLimit,
            HonuaExceededTransferLimit: honuaPage.ExceededTransferLimit,
            SourceRowsReturned: sourcePage.Rows.Count,
            HonuaRowsReturned: honuaPage.Rows.Count);
    }

    private static async Task<double[]> MeasureQueryLatenciesAsync(
        HttpClient client,
        string queryEndpoint,
        string orderByField,
        string[] outFields)
    {
        const int warmupCount = 2;
        const int sampleCount = 9;
        var samples = new List<double>(sampleCount);

        for (var i = 0; i < warmupCount + sampleCount; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await QueryRowsPageAsync(
                client,
                queryEndpoint,
                orderByField,
                outFields,
                0,
                50,
                returnGeometry: false);
            stopwatch.Stop();

            if (i >= warmupCount)
            {
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        return samples.ToArray();
    }

    private static double ComputePercentile(double[] samples, double percentile)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var ordered = samples.OrderBy(static value => value).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var weight = position - lowerIndex;
        return ordered[lowerIndex] + ((ordered[upperIndex] - ordered[lowerIndex]) * weight);
    }

    private async Task RecordScorecardForServiceAsync(
        ParityServiceCase serviceCase,
        SourceLayerSnapshot sourceSnapshot,
        PublishedLayerHandle publishedLayer,
        string sourceQueryEndpoint,
        string honuaQueryEndpoint,
        string sanitizedCompareField,
        string sanitizedNumericField,
        string? sanitizedDateField)
    {
        var latencyMetrics = await CaptureLatencyMetricsAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            serviceCase,
            honuaQueryEndpoint,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField);
        var transferLimitMetrics = await CaptureTransferLimitMetricsAsync(
            sourceSnapshot,
            sourceQueryEndpoint,
            serviceCase,
            honuaQueryEndpoint,
            sanitizedCompareField,
            sanitizedNumericField,
            sanitizedDateField);

        var checks = new List<ParityCheckResult>
        {
            new("core_query_parity", true, true),
            new("all_fields_diff", true, true),
            new("edge_case_query_parity", true, true),
            new("error_shape_parity", true, true),
            new("statistics_parity", true, true),
            new("grouped_statistics_parity", true, true),
            new("return_ids_only_parity", true, true),
            new("distinct_parity", true, true),
            new("spatial_envelope_parity", true, true),
            new("geometry_parity", true, true),
            new("mapserver_featureserver_parity", true, true),
            new(
                "transfer_limit_flag_parity",
                true,
                transferLimitMetrics.SourceExceededTransferLimit == transferLimitMetrics.HonuaExceededTransferLimit,
                $"source={transferLimitMetrics.SourceExceededTransferLimit}, honua={transferLimitMetrics.HonuaExceededTransferLimit}, pageSize={transferLimitMetrics.PageSize}"),
            new(
                "time_query_parity",
                sourceSnapshot.SupportsTimeQuery,
                true,
                sourceSnapshot.SupportsTimeQuery ? null : "source layer has no timeInfo"),
            new(
                "geojson_query_parity",
                sourceSnapshot.SupportsGeoJsonQuery,
                true,
                sourceSnapshot.SupportsGeoJsonQuery ? null : "source layer does not advertise geojson support")
        };

        _scorecardEntries.Add(new ParityScorecardEntry(
            ServiceCase: serviceCase.Name,
            SourceServiceUrl: serviceCase.ServiceUrl,
            SourceLayerId: serviceCase.LayerId,
            HonuaServiceName: publishedLayer.ServiceName,
            HonuaLayerId: publishedLayer.LayerId,
            SourceFeatureCount: sourceSnapshot.TotalCount,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Checks: checks,
            LatencyMetrics: latencyMetrics,
            TransferLimitMetrics: transferLimitMetrics));
    }

    private async Task WriteScorecardArtifactAsync()
    {
        if (_scorecardEntries.Count == 0)
        {
            return;
        }

        var payload = new ParityScorecardArtifact(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Cases: _scorecardEntries.ToArray());

        var directory = Path.Combine(Path.GetTempPath(), "honua-parity-scorecards");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(
            directory,
            $"geoservices-parity-scorecard-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        var json = JsonSerializer.Serialize(payload, _scorecardJsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    private async Task CleanupImportedTablesAsync()
    {
        if (_importedTables.Count == 0 || string.IsNullOrWhiteSpace(_schema))
        {
            return;
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        var schemas = new HashSet<string>(StringComparer.Ordinal)
        {
            _schema,
            "public"
        };

        foreach (var table in _importedTables.Distinct(StringComparer.Ordinal))
        {
            foreach (var schema in schemas)
            {
                await using var dropCommand = connection.CreateCommand();
                dropCommand.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(schema)}.{QuoteIdentifier(table)};";
                await dropCommand.ExecuteNonQueryAsync();
            }
        }
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task<string> ReadJsonPayloadAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadAsStringAsync();
    }

    private static GeoservicesImportStatus ReadStatus(JsonElement statusElement)
    {
        return statusElement.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse(statusElement.GetString(), out GeoservicesImportStatus parsed)
                ? parsed
                : throw new InvalidOperationException("Import status is invalid."),
            JsonValueKind.Number => (GeoservicesImportStatus)statusElement.GetInt32(),
            _ => throw new InvalidOperationException("Import status is invalid.")
        };
    }

    private sealed record ParityServiceCase(
        string Name,
        string ServiceUrl,
        int LayerId,
        string CompareField,
        string NumericField,
        string? DateField = null,
        bool ValidateExtentParity = true);

    private sealed record SourceLayerSnapshot(
        string ObjectIdField,
        int TotalCount,
        string? GeometryType,
        bool SupportsTimeQuery,
        bool SupportsGeoJsonQuery,
        IReadOnlyList<FieldParityMapping> ComparableFields,
        IReadOnlySet<string> ExpectedImportedFields,
        StringFieldStats CompareStats,
        NumericFieldStats NumericStats,
        DateFieldStats? DateStats,
        ExtentStats? Extent4326,
        IReadOnlyList<Dictionary<string, JsonElement>> Rows,
        IReadOnlyList<string> SampleValues);

    private sealed record StringFieldStats(long TotalCount, long NullCount);

    private sealed record NumericFieldStats(long TotalCount, long NullCount, double? Min, double? Max);

    private sealed record DateFieldStats(long TotalCount, long NullCount, long? MinEpochMs, long? MaxEpochMs);

    private sealed record ExtentStats(double XMin, double YMin, double XMax, double YMax);

    private sealed record PublishedLayerHandle(int LayerId, string ServiceName);

    private sealed record AppendFeatureRow(
        IReadOnlyDictionary<string, object?> Attributes,
        IReadOnlyDictionary<string, object?>? Geometry);

    private sealed record AppendBatchRows(IReadOnlyList<AppendFeatureRow> Rows);

    private sealed record FeatureRowWithGeometry(
        IReadOnlyDictionary<string, JsonElement> Attributes,
        JsonElement? Geometry);

    private sealed record GeometrySummary(
        string Kind,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY,
        int VertexCount,
        string Hash);

    private sealed record GeometrySignatureEntry(
        string Semantic,
        string Kind,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY,
        int VertexCount,
        string Hash);

    private sealed record QueryPageResult(
        IReadOnlyList<Dictionary<string, JsonElement>> Rows,
        IReadOnlyList<FeatureRowWithGeometry> Features,
        bool HasGeometry,
        bool ExceededTransferLimit);

    private sealed record GeoJsonQueryPageResult(
        IReadOnlyList<Dictionary<string, JsonElement>> Rows,
        bool HasGeometry);

    private sealed record FieldParityMapping(
        string SourceName,
        string SanitizedName,
        string SourceType);

    private sealed record ErrorSignature(
        bool IsError,
        HttpStatusCode StatusCode,
        int? NormalizedCode,
        int ErrorFamily,
        string Message);

    private sealed record ParityErrorCase(
        string Name,
        string SourceRequestUri,
        string HonuaRequestUri);

    private sealed record ParityCheckResult(
        string Name,
        bool Applicable,
        bool Passed,
        string? Notes = null);

    private sealed record ParityScorecardEntry(
        string ServiceCase,
        string SourceServiceUrl,
        int SourceLayerId,
        string HonuaServiceName,
        int HonuaLayerId,
        int SourceFeatureCount,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<ParityCheckResult> Checks,
        ParityLatencyMetrics LatencyMetrics,
        ParityTransferLimitMetrics TransferLimitMetrics);

    private sealed record ParityScorecardArtifact(
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<ParityScorecardEntry> Cases);

    private sealed record ParityLatencyMetrics(
        int SampleCount,
        double SourceP50Ms,
        double SourceP95Ms,
        double SourceP99Ms,
        double HonuaP50Ms,
        double HonuaP95Ms,
        double HonuaP99Ms,
        double? HonuaToSourceP95Ratio,
        double? HonuaToSourceP99Ratio);

    private sealed record ParityTransferLimitMetrics(
        int PageSize,
        bool SourceExceededTransferLimit,
        bool HonuaExceededTransferLimit,
        int SourceRowsReturned,
        int HonuaRowsReturned);
}
