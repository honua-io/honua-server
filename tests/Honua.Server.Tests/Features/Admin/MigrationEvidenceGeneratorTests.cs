// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Migration.Domain;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin;

namespace Honua.Server.Tests.Features.Admin;

public sealed class MigrationEvidenceGeneratorTests
{
    [Fact]
    public void CanonicalizeFeatureRows_WithGeoJsonPayload_UsesMappedFieldNamesBeforeCanonicalFallback()
    {
        using var sourcePayload = JsonDocument.Parse("""
            {
              "features": [
                {
                  "properties": {
                    "Parcel ID": 101,
                    "Owner Name": "Alpha"
                  }
                }
              ]
            }
            """);
        using var targetPayload = JsonDocument.Parse("""
            {
              "features": [
                {
                  "properties": {
                    "parcel_id": 101,
                    "owner_name": "Alpha"
                  }
                }
              ]
            }
            """);

        var fieldMappings = new MigrationEvidenceGenerator.FieldMappingSet(
            [
                new MigrationEvidenceGenerator.FieldMappingEntry("Parcel ID", "parcel_id", "parcelid", "esriFieldTypeInteger"),
                new MigrationEvidenceGenerator.FieldMappingEntry("Owner Name", "owner_name", "ownername", "esriFieldTypeString")
            ],
            StringField: null,
            NumericField: null,
            DateField: null);

        var sourceRows = MigrationEvidenceGenerator.CanonicalizeFeatureRows(
            sourcePayload.RootElement,
            fieldMappings,
            MigrationEvidenceGenerator.FeatureRowFieldOrigin.Source,
            geoJson: true);
        var targetRows = MigrationEvidenceGenerator.CanonicalizeFeatureRows(
            targetPayload.RootElement,
            fieldMappings,
            MigrationEvidenceGenerator.FeatureRowFieldOrigin.Target,
            geoJson: true);

        sourceRows.Should().Equal(["parcelid=101|ownername=Alpha"]);
        targetRows.Should().Equal(sourceRows);
    }

    [Fact]
    public async Task BuildReturnIdsOnlyCheckAsync_WhenObjectIdsMismatch_ReturnsFail()
    {
        var sourceClient = CreateHttpClient(
            BuildRequest(),
            sourceResponseBody: """
                {
                  "objectIds": [1, 2, 3]
                }
                """,
            targetResponseBody: """
                {
                  "objectIds": [1, 2, 4]
                }
                """);

        var method = typeof(MigrationEvidenceGenerator).GetMethod(
            "BuildReturnIdsOnlyCheckAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var request = new MigrationEvidenceRequest
        {
            Provider = MigrationEvidenceProvider.ArcGisGeoservices,
            SourceServiceUrl = "http://source.example.local",
            TargetBaseUrl = "http://target.example.local",
            TargetServiceName = "migration-service",
            CutoverProfile = MigrationCutoverProfile.Pilot,
            RollbackPlanReference = "rollback-1",
            Layers = [new MigrationEvidenceLayerMapping { SourceLayerId = 1, TargetLayerId = 3 }]
        };

        var task = (Task<MigrationComparisonCheck>)method!.Invoke(
            null,
            [sourceClient, sourceClient, request, request.Layers[0], CancellationToken.None])!;
        var check = await task;

        check.CheckName.Should().Be("return_ids_only_parity");
        check.Scope.Should().Be("1->3");
        check.Status.Should().Be(MigrationEvidenceStatus.Fail);
    }

    [Fact]
    public async Task BuildReturnIdsOnlyCheckAsync_WhenObjectIdsMatchInDifferentOrder_Passes()
    {
        var sourceClient = CreateHttpClient(
            BuildRequest(),
            sourceResponseBody: """
                {
                  "objectIds": [1, 2, 3]
                }
                """,
            targetResponseBody: """
                {
                  "objectIds": [3, 1, 2]
                }
                """);

        var method = typeof(MigrationEvidenceGenerator).GetMethod(
            "BuildReturnIdsOnlyCheckAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var request = new MigrationEvidenceRequest
        {
            Provider = MigrationEvidenceProvider.ArcGisGeoservices,
            SourceServiceUrl = "http://source.example.local",
            TargetBaseUrl = "http://target.example.local",
            TargetServiceName = "migration-service",
            CutoverProfile = MigrationCutoverProfile.Pilot,
            RollbackPlanReference = "rollback-2",
            Layers = [new MigrationEvidenceLayerMapping { SourceLayerId = 1, TargetLayerId = 3 }]
        };

        var task = (Task<MigrationComparisonCheck>)method!.Invoke(
            null,
            [sourceClient, sourceClient, request, request.Layers[0], CancellationToken.None])!;
        var check = await task;

        check.Status.Should().Be(MigrationEvidenceStatus.Pass);
    }

    [Fact]
    public async Task BuildGeoJsonParityCheckAsync_WhenFeaturesMatchInDifferentOrder_Passes()
    {
        var request = BuildRequest([new MigrationEvidenceLayerMapping { SourceLayerId = 1, TargetLayerId = 3 }]);

        using var sourceMetadata = JsonDocument.Parse("""
            {
              "supportedQueryFormats": "json,geojson"
            }
            """);
        using var targetMetadata = JsonDocument.Parse("""
            {
              "supportedQueryFormats": ["json", "geojson"]
            }
            """);

        var fieldMappings = new MigrationEvidenceGenerator.FieldMappingSet(
            [
                new MigrationEvidenceGenerator.FieldMappingEntry("Parcel ID", "parcel_id", "parcelid", "esriFieldTypeInteger"),
                new MigrationEvidenceGenerator.FieldMappingEntry("Owner Name", "owner_name", "ownername", "esriFieldTypeString")
            ],
            StringField: null,
            NumericField: null,
            DateField: null);

        using var client = new HttpClient(
            new GeoJsonStubHttpMessageHandler(
                request,
                sourceResponseBody: """
                    {
                      "features": [
                        {
                          "properties": {
                            "Parcel ID": 101,
                            "Owner Name": "Alpha"
                          }
                        },
                        {
                          "properties": {
                            "Parcel ID": 202,
                            "Owner Name": "Bravo"
                          }
                        }
                      ]
                    }
                    """,
                targetResponseBody: """
                    {
                      "features": [
                        {
                          "properties": {
                            "parcel_id": 202,
                            "owner_name": "Bravo"
                          }
                        },
                        {
                          "properties": {
                            "parcel_id": 101,
                            "owner_name": "Alpha"
                          }
                        }
                      ]
                    }
                    """));

        var check = await InvokePrivateAsync<MigrationComparisonCheck>(
            "BuildGeoJsonParityCheckAsync",
            client,
            client,
            request,
            request.Layers[0],
            sourceMetadata.RootElement,
            targetMetadata.RootElement,
            fieldMappings,
            2,
            CancellationToken.None);

        check.CheckName.Should().Be("geojson_query_parity");
        check.Scope.Should().Be("1->3");
        check.Status.Should().Be(MigrationEvidenceStatus.Pass);
    }

    [Fact]
    public async Task BuildTransferLimitCheckAsync_WhenMetadataUsesCustomObjectIdField_UsesResolvedField()
    {
        var request = BuildRequest([new MigrationEvidenceLayerMapping { SourceLayerId = 1, TargetLayerId = 3 }]);

        using var sourceMetadata = JsonDocument.Parse("""
            {
              "objectIdField": "source_oid"
            }
            """);
        using var targetMetadata = JsonDocument.Parse("""
            {
              "fields": [
                {
                  "name": "target_oid",
                  "type": "esriFieldTypeOID"
                }
              ]
            }
            """);

        var handler = new TransferLimitStubHttpMessageHandler(request, "source_oid", "target_oid");
        using var client = new HttpClient(handler);

        var check = await InvokePrivateAsync<MigrationComparisonCheck>(
            "BuildTransferLimitCheckAsync",
            client,
            client,
            request,
            request.Layers[0],
            sourceMetadata.RootElement,
            targetMetadata.RootElement,
            2,
            CancellationToken.None);

        check.CheckName.Should().Be("transfer_limit_flag_parity");
        check.Scope.Should().Be("1->3");
        check.Status.Should().Be(MigrationEvidenceStatus.Pass);
        handler.SourceOrderByField.Should().Be("source_oid");
        handler.TargetOrderByField.Should().Be("target_oid");
    }

    private static MigrationEvidenceRequest BuildRequest(MigrationEvidenceLayerMapping[]? layers = null) =>
        new()
        {
            Provider = MigrationEvidenceProvider.ArcGisGeoservices,
            SourceServiceUrl = "http://source.example.local",
            TargetBaseUrl = "http://target.example.local",
            TargetServiceName = "migration-service",
            CutoverProfile = MigrationCutoverProfile.Pilot,
            RollbackPlanReference = "rollback-plan",
            Layers = layers ?? []
        };

    private static async Task<T> InvokePrivateAsync<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(MigrationEvidenceGenerator).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var task = method!.Invoke(null, arguments);
        task.Should().BeAssignableTo<Task<T>>();
        return await (Task<T>)task!;
    }

    private static HttpClient CreateHttpClient(
        MigrationEvidenceRequest request,
        string sourceResponseBody,
        string targetResponseBody)
    {
        var handler = new ReturnIdsOnlyStubHttpMessageHandler(request, sourceResponseBody, targetResponseBody);
        return new HttpClient(handler);
    }

    private sealed class ReturnIdsOnlyStubHttpMessageHandler(
        MigrationEvidenceRequest request,
        string sourceResponseBody,
        string targetResponseBody) : HttpMessageHandler
    {
        private readonly string _sourcePath = BuildSourceLayerQueryUrl(request.SourceServiceUrl, 1);
        private readonly string _targetPath = BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, 3);
        private readonly HttpResponseMessage _sourceResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(sourceResponseBody)
        };
        private readonly HttpResponseMessage _targetResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(targetResponseBody)
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult((request.RequestUri?.AbsolutePath, request.Method.Method) switch
            {
                (var path, _) when path == _sourcePath => _sourceResponse,
                (var path, _) when path == _targetPath => _targetResponse,
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            });
        }

        private static string BuildSourceLayerQueryUrl(string serviceUrl, int layerId)
            => new Uri($"{serviceUrl.TrimEnd('/')}/{layerId}/query").AbsolutePath;

        private static string BuildTargetLayerQueryUrl(string targetBaseUrl, string serviceName, int layerId)
            => new Uri($"{targetBaseUrl.TrimEnd('/')}/rest/services/{Uri.EscapeDataString(serviceName)}/FeatureServer/{layerId}/query").AbsolutePath;
    }

    private sealed class GeoJsonStubHttpMessageHandler(
        MigrationEvidenceRequest request,
        string sourceResponseBody,
        string targetResponseBody) : HttpMessageHandler
    {
        private readonly string _sourcePath = BuildSourceLayerQueryUrl(request.SourceServiceUrl, 1);
        private readonly string _targetPath = BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, 3);
        private readonly string _sourceResponseBody = sourceResponseBody;
        private readonly string _targetResponseBody = targetResponseBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult((request.RequestUri?.AbsolutePath, request.Method.Method) switch
            {
                (var path, _) when path == _sourcePath => CreateJsonResponse(_sourceResponseBody),
                (var path, _) when path == _targetPath => CreateJsonResponse(_targetResponseBody),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            });
        }
    }

    private sealed class TransferLimitStubHttpMessageHandler(
        MigrationEvidenceRequest request,
        string expectedSourceOrderByField,
        string expectedTargetOrderByField) : HttpMessageHandler
    {
        private readonly string _sourcePath = BuildSourceLayerQueryUrl(request.SourceServiceUrl, 1);
        private readonly string _targetPath = BuildTargetLayerQueryUrl(request.TargetBaseUrl, request.TargetServiceName, 3);

        public string? SourceOrderByField { get; private set; }

        public string? TargetOrderByField { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var orderByField = GetQueryParameter(request.RequestUri, "orderByFields");

            return Task.FromResult((request.RequestUri?.AbsolutePath, request.Method.Method) switch
            {
                (var path, _) when path == _sourcePath => HandleSourceRequest(orderByField, request),
                (var path, _) when path == _targetPath => HandleTargetRequest(orderByField, request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            });
        }

        private HttpResponseMessage HandleSourceRequest(string? orderByField, HttpRequestMessage request)
        {
            SourceOrderByField = orderByField;
            return string.Equals(orderByField, expectedSourceOrderByField, StringComparison.Ordinal)
                ? CreateJsonResponse("""
                    {
                      "features": [],
                      "exceededTransferLimit": true
                    }
                    """)
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { RequestMessage = request };
        }

        private HttpResponseMessage HandleTargetRequest(string? orderByField, HttpRequestMessage request)
        {
            TargetOrderByField = orderByField;
            return string.Equals(orderByField, expectedTargetOrderByField, StringComparison.Ordinal)
                ? CreateJsonResponse("""
                    {
                      "features": [],
                      "exceededTransferLimit": true
                    }
                    """)
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { RequestMessage = request };
        }
    }

    private static HttpResponseMessage CreateJsonResponse(string body) =>
        new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(body)
        };

    private static string? GetQueryParameter(Uri? requestUri, string key)
    {
        if (requestUri is null || string.IsNullOrWhiteSpace(requestUri.Query))
        {
            return null;
        }

        foreach (var pair in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var parameterName = Uri.UnescapeDataString(parts[0]);
            if (!string.Equals(parameterName, key, StringComparison.Ordinal))
            {
                continue;
            }

            return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return null;
    }

    private static string BuildSourceLayerQueryUrl(string serviceUrl, int layerId)
        => new Uri($"{serviceUrl.TrimEnd('/')}/{layerId}/query").AbsolutePath;

    private static string BuildTargetLayerQueryUrl(string targetBaseUrl, string serviceName, int layerId)
        => new Uri($"{targetBaseUrl.TrimEnd('/')}/rest/services/{Uri.EscapeDataString(serviceName)}/FeatureServer/{layerId}/query").AbsolutePath;
}
