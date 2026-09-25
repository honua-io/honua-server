// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// #4599: ArcGIS source reads go through the published Honua.Sdk.GeoServices client. These cases pin
/// the wire shapes the SDK sends for every supported source root, the values the adapter hands the
/// import pipeline, and the server-owned transport guarantees (credential headers, SSRF guard, retry
/// and Retry-After, HTTP-200 error envelopes, sanitized errors, bounded bodies, cancellation).
/// </summary>
public sealed class ArcGisRestClientSourceSdkTests
{
    private const string PointFeatures = """
        {
          "objectIdFieldName": "OBJECTID",
          "spatialReference": { "wkid": 102100, "latestWkid": 3857 },
          "exceededTransferLimit": true,
          "features": [
            { "attributes": { "OBJECTID": 17, "NAME": "Hydrant A", "PRESSURE": 61.5 }, "geometry": { "x": -157.81, "y": 21.3, "z": 12.25, "m": 3.5 } },
            { "attributes": { "OBJECTID": 18, "NAME": null, "PRESSURE": 58 }, "geometry": { "x": -157.82, "y": 21.31, "z": 13.5, "m": 4 } }
          ]
        }
        """;

    public static TheoryData<string, string> SourceRoots => new()
    {
        { "https://services.arcgis.com/Org123/arcgis/rest/services/Hydrants/FeatureServer", "/Org123/arcgis/rest/services/Hydrants/FeatureServer" },
        { "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", "/arcgis/rest/services/Inspections/FeatureServer" },
        { "https://gis.example.com/arcgis/rest/services/Utilities/Water/FeatureServer/", "/arcgis/rest/services/Utilities/Water/FeatureServer" },
        { "https://gis.example.com/arcgis/rest/services/Basemap/Parcels/MapServer", "/arcgis/rest/services/Basemap/Parcels/MapServer" },
    };

    [Theory]
    [MemberData(nameof(SourceRoots))]
    public async Task QueryFeaturesAsync_AddressesEverySourceRootShape_AndReturnsSourceValues(string serviceUrl, string expectedServicePath)
    {
        var handler = new ScriptedHandler(_ => Json(PointFeatures));
        var client = CreateClient(handler);

        var result = await client.QueryFeaturesAsync(
            serviceUrl, 3, offset: 40, batchSize: 2, whereClause: "STATUS = 'Active'", outFields: ["OBJECTID", "NAME", "PRESSURE"],
            outSrid: 4326, timeoutSeconds: 5, maxRetries: 0, CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.AbsolutePath.Should().Be(expectedServicePath + "/3/query");
        var query = ParseQuery(request.Uri);
        query.Should().Contain("where", "STATUS = 'Active'")
            .And.Contain("outFields", "OBJECTID,NAME,PRESSURE")
            .And.Contain("returnGeometry", "true")
            .And.Contain("returnZ", "true")
            .And.Contain("returnM", "true")
            .And.Contain("resultOffset", "40")
            .And.Contain("resultRecordCount", "2")
            .And.Contain("outSR", "4326")
            .And.Contain("f", "json")
            .And.NotContainKey("objectIds");

        result.ExceededTransferLimit.Should().BeTrue();
        result.SpatialReferenceWkid.Should().Be(102100);
        result.Features.Should().HaveCount(2);
        result.Features[0].Attributes!["OBJECTID"].GetInt64().Should().Be(17);
        result.Features[0].Attributes!["NAME"].GetString().Should().Be("Hydrant A");
        result.Features[0].Attributes!["PRESSURE"].GetDouble().Should().Be(61.5);
        result.Features[1].Attributes!["NAME"].ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        var geometry = result.Features[1].Geometry!.Value;
        geometry.GetProperty("x").GetDouble().Should().Be(-157.82);
        geometry.GetProperty("z").GetDouble().Should().Be(13.5);
        geometry.GetProperty("m").GetDouble().Should().Be(4);
    }

    [Theory]
    [MemberData(nameof(SourceRoots))]
    public async Task MetadataReads_UseEverySourceRoot_AndPreserveRawSourcePresence(string serviceUrl, string expectedServicePath)
    {
        const string metadata = """
            {"id":3,"name":"Inspection","type":"Feature Layer",
             "extent":{"xmin":1,"ymin":2,"xmax":3,"ymax":4,"spatialReference":{"wkt":"LOCAL_CS[\"Survey\"]","vendorFlag":true}},
             "fields":[{"name":"OMITTED","type":"esriFieldTypeString"},
                       {"name":"REQUIRED","type":"esriFieldTypeString","nullable":false,"editable":false},
                       {"name":"OPTIONAL","type":"esriFieldTypeString","nullable":true,"editable":true}],
             "vendorMetadata":{"largeId":9007199254740993,"explicitNull":null}}
            """;
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/query", StringComparison.Ordinal)
            ? Json("""{"count":2}""")
            : Json(metadata));
        var client = CreateClient(handler);
        var credentials = TokenCredentials("metadata-secret");

        using var service = await client.GetServiceMetadataAsync(serviceUrl, 0, 5, credentials, CancellationToken.None);
        using var layer = await client.GetLayerMetadataAsync(serviceUrl, 3, 0, 5, credentials, CancellationToken.None);
        var typed = await client.GetLayerInfoAsync(serviceUrl, 3, 5, 0, CancellationToken.None, credentials);

        service.RootElement.GetRawText().Should().Be(metadata);
        layer.RootElement.GetRawText().Should().Be(metadata);
        layer.RootElement.TryGetProperty("hasAttachments", out _).Should().BeFalse();
        layer.RootElement.GetProperty("fields")[0].TryGetProperty("nullable", out _).Should().BeFalse();
        layer.RootElement.GetProperty("extent").GetProperty("spatialReference").TryGetProperty("wkid", out _).Should().BeFalse();
        layer.RootElement.GetProperty("vendorMetadata").GetProperty("largeId").GetInt64().Should().Be(9_007_199_254_740_993);
        typed.Fields.Select(static field => field.Nullable).Should().Equal(true, false, true);
        typed.SpatialReferenceWkid.Should().BeNull();
        typed.MaxRecordCount.Should().BeNull();
        typed.FeatureCount.Should().Be(2);
        handler.Requests.Select(static request => request.Uri.AbsolutePath).Should().Equal(
            expectedServicePath, expectedServicePath + "/3", expectedServicePath + "/3", expectedServicePath + "/3/query");
        handler.Requests.Should().OnlyContain(request => request.EsriAuthorization == "Bearer metadata-secret"
            && !request.Uri.Query.Contains("metadata-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawMetadata_RetriesTransientTransportFailure_WithoutLosingUnknownProperties(bool layer)
    {
        var handler = new ScriptedHandler(request => request.Attempt == 1
            ? Json("{}", HttpStatusCode.ServiceUnavailable)
            : Json("""{"vendorProperty":null}"""));
        var client = CreateClient(handler);

        using var document = await ReadRawMetadataAsync(client, layer, maxRetries: 1);

        document.RootElement.GetProperty("vendorProperty").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawMetadata_RejectsDeclaredOversizedBody_WithoutRetry(bool layer)
    {
        var handler = new ScriptedHandler(_ => new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(MigrationHttpContentReader.DefaultMaxResponseBytes + 1)
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => ReadRawMetadataAsync(client, layer, maxRetries: 2));

        exception.Message.Should().Contain("exceeding");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawMetadata_MalformedJson_ReportsSanitizedErrorWithoutRetry(bool layer)
    {
        var handler = new ScriptedHandler(_ => Json("{invalid-source-secret"));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadRawMetadataAsync(client, layer, maxRetries: 2));

        exception.Message.Should().Contain("Failed to parse ArcGIS JSON").And.NotContain("invalid-source-secret");
        exception.InnerException.Should().BeNull();
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawMetadata_CallerCancellation_PropagatesWithoutRetry(bool layer)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Json("{}");
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadRawMetadataAsync(client, layer, 2, cancellation.Token));

        handler.Requests.Should().ContainSingle();
    }

    private static Task<System.Text.Json.JsonDocument> ReadRawMetadataAsync(
        ArcGisRestClient client, bool layer, int maxRetries, CancellationToken cancellationToken = default)
    {
        const string source = "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer";
        return layer
            ? client.GetLayerMetadataAsync(source, 3, maxRetries, 5, null, cancellationToken)
            : client.GetServiceMetadataAsync(source, maxRetries, 5, null, cancellationToken);
    }

    [Fact]
    public async Task QueryFeaturesAsync_ObjectIdWindow_SendsIdsWithoutOffsetPaging()
    {
        var handler = new ScriptedHandler(_ => Json(PointFeatures));
        var client = CreateClient(handler);

        await client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, offset: 0, batchSize: 1000,
            whereClause: null, outFields: null, outSrid: null, timeoutSeconds: 5, maxRetries: 0, CancellationToken.None,
            objectIds: [17, 18]);

        var query = ParseQuery(handler.Requests.Single().Uri);
        query.Should().Contain("objectIds", "17,18")
            .And.Contain("where", "1=1")
            .And.Contain("outFields", "*")
            .And.NotContainKey("resultOffset")
            .And.NotContainKey("resultRecordCount");
    }

    [Fact]
    public async Task QueryFeaturesAsync_EmptyObjectIdWindow_SendsNoRequest()
    {
        var handler = new ScriptedHandler(_ => Json(PointFeatures));
        var client = CreateClient(handler);

        var result = await client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 1000, null, null, null, 5, 0,
            CancellationToken.None, objectIds: []);

        result.Features.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryFeaturesAsync_LongObjectIdWindow_PostsTheQueryWithCredentialHeader()
    {
        var handler = new ScriptedHandler(_ => Json(PointFeatures));
        var client = CreateClient(handler);
        var objectIds = Enumerable.Range(1_000_000, 400).Select(static id => (long)id).ToArray();

        await client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 1000, null, null, null, 5, 0,
            CancellationToken.None, TokenCredentials("post-secret"), objectIds);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/arcgis/rest/services/Inspections/FeatureServer/0/query");
        request.Uri.Query.Should().BeEmpty();
        request.EsriAuthorization.Should().Be("Bearer post-secret");
        var form = ParseForm(request.Body!);
        form["objectIds"].Should().Be(string.Join(',', objectIds));
        form["returnZ"].Should().Be("true");
        request.Body.Should().NotContain("post-secret");
    }

    [Fact]
    public async Task QueryObjectIdsAndCount_ReturnSourceIdsAndCount()
    {
        var handler = new ScriptedHandler(request => ParseQuery(request.RequestUri!).ContainsKey("returnIdsOnly")
            ? Json("""{ "objectIdFieldName": "OBJECTID", "objectIds": [5, 9, 3000000001] }""")
            : Json("""{ "count": 3000000002 }"""));
        var client = CreateClient(handler);
        const string serviceUrl = "https://services.arcgis.com/Org123/arcgis/rest/services/Hydrants/FeatureServer";

        var ids = await client.QueryObjectIdsAsync(serviceUrl, 2, "TYPE = 1", 5, 0, CancellationToken.None);
        var count = await client.QueryFeatureCountAsync(serviceUrl, 2, "  ", 5, 0, CancellationToken.None);

        ids.Should().Equal(5, 9, 3_000_000_001);
        count.Should().Be(3_000_000_002);
        ParseQuery(handler.Requests[0].Uri).Should().Contain("where", "TYPE = 1").And.Contain("returnIdsOnly", "true");
        ParseQuery(handler.Requests[1].Uri).Should().Contain("where", "1=1").And.Contain("returnCountOnly", "true");
        handler.Requests.Should().OnlyContain(r => r.Uri.AbsolutePath == "/Org123/arcgis/rest/services/Hydrants/FeatureServer/2/query");
    }

    [Fact]
    public async Task DiscoverServiceAsync_MapsServiceRootMetadataFromTheSdkModel()
    {
        var handler = new ScriptedHandler(_ => Json("""
            {
              "currentVersion": 11.1,
              "serviceDescription": "Water utility",
              "description": "Hydrants and valves",
              "maxRecordCount": 2000,
              "capabilities": "Query, Extract",
              "supportedQueryFormats": "JSON, geoJSON, PBF",
              "spatialReference": { "wkid": 102100, "latestWkid": 3857 },
              "layers": []
            }
            """));
        var client = CreateClient(handler);

        var service = await client.DiscoverServiceAsync(
            "https://gis.example.com/arcgis/rest/services/Utilities/Water/FeatureServer", 5, 0, CancellationToken.None);

        handler.Requests.Single().Uri.PathAndQuery.Should().Be("/arcgis/rest/services/Utilities/Water/FeatureServer?f=json");
        service.ServiceUrl.Should().Be("https://gis.example.com/arcgis/rest/services/Utilities/Water/FeatureServer");
        service.ServiceName.Should().Be("Water utility");
        service.Description.Should().Be("Hydrants and valves");
        service.Version.Should().Be("11.1");
        service.MaxRecordCount.Should().Be(2000);
        service.SpatialReferenceWkid.Should().Be(102100);
        service.Capabilities.Should().Equal("Query", "Extract");
        service.SupportedQueryFormats.Should().Equal("JSON", "geoJSON", "PBF");
    }

    [Fact]
    public async Task DiscoverServiceAsync_OmittedMaxRecordCountAndSpatialReference_StayUnadvertised()
    {
        var handler = new ScriptedHandler(_ => Json("""{ "layers": [] }"""));
        var client = CreateClient(handler);

        var service = await client.DiscoverServiceAsync(
            "https://gis.example.com/arcgis/rest/services/Basemap/Parcels/MapServer", 5, 0, CancellationToken.None);

        service.ServiceName.Should().Be("Parcels");
        service.MaxRecordCount.Should().BeNull();
        service.SpatialReferenceWkid.Should().BeNull();
        service.Description.Should().BeNull();
        service.Version.Should().BeNull();
        service.SupportedQueryFormats.Should().Equal("JSON");
    }

    [Fact]
    public async Task QueryAttachmentsAsync_ReturnsSourceAttachmentMetadata()
    {
        var handler = new ScriptedHandler(_ => Json("""
            {
              "attachmentGroups": [
                {
                  "parentObjectId": 17,
                  "parentGlobalId": "{6A3C0D5B-1F0E-4C55-9A2B-7D5E4B7B8C10}",
                  "attachmentInfos": [
                    { "id": 501, "name": "valve.jpg", "contentType": "image/jpeg", "size": 20480, "keywords": "inspection" },
                    { "id": 502, "name": "notes.pdf", "contentType": "application/pdf", "size": 1024 }
                  ]
                }
              ]
            }
            """));
        var client = CreateClient(handler);

        var response = await client.QueryAttachmentsAsync(
            "https://services.arcgis.com/Org123/arcgis/rest/services/Hydrants/FeatureServer", 0, [17, 18], 5, 0, CancellationToken.None);

        var request = handler.Requests.Single();
        request.Uri.AbsolutePath.Should().Be("/Org123/arcgis/rest/services/Hydrants/FeatureServer/0/queryAttachments");
        ParseQuery(request.Uri).Should().Contain("objectIds", "17,18").And.Contain("returnUrl", "false");
        var group = response.AttachmentGroups.Should().ContainSingle().Subject;
        group.ParentObjectId.Should().Be(17);
        group.ParentGlobalId.Should().Be("{6A3C0D5B-1F0E-4C55-9A2B-7D5E4B7B8C10}");
        group.AttachmentInfos!.Select(static info => (info.Id, info.Name, info.ContentType, info.Size, info.Keywords))
            .Should().Equal(
                (501L, "valve.jpg", "image/jpeg", 20480L, "inspection"),
                (502L, "notes.pdf", "application/pdf", 1024L, (string?)null));
    }

    [Fact]
    public async Task QueryAttachmentsAsync_ErrorEnvelope_IsRejectedInsteadOfReadAsNoAttachments()
    {
        var handler = new ScriptedHandler(_ => Json("""{ "error": { "code": 400, "message": "queryAttachments not supported" } }"""));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAttachmentsAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, [17], 5, 2, CancellationToken.None));

        exception.Message.Should().StartWith("ArcGIS response error 400 for 'https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer/0/queryAttachments'")
            .And.Contain("queryAttachments not supported");
        handler.Requests.Should().ContainSingle("an HTTP-200 error envelope is a definitive answer and is not retried");
    }

    [Fact]
    public async Task DownloadAttachmentAsync_StreamsSourceBytesWithCredentialHeader()
    {
        var payload = Enumerable.Range(0, 70_000).Select(static i => (byte)(i % 251)).ToArray();
        var handler = new ScriptedHandler(_ =>
        {
            var response = new CallerOwnedHttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return response;
        });
        var client = CreateClient(handler);

        await using (var download = await client.DownloadAttachmentAsync(
            "https://gis.example.com/arcgis/rest/services/Utilities/Water/MapServer", 4, 17, 501, 5, 0, CancellationToken.None,
            TokenCredentials("download-secret")))
        {
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal(payload);
            download.ContentType.Should().Be("image/jpeg");
            download.ContentLength.Should().Be(payload.Length);
        }

        var request = handler.Requests.Single();
        request.Uri.AbsolutePath.Should().Be("/arcgis/rest/services/Utilities/Water/MapServer/4/17/attachments/501");
        request.Uri.Query.Should().BeEmpty();
        request.EsriAuthorization.Should().Be("Bearer download-secret");
    }

    [Fact]
    public async Task DownloadAttachmentAsync_DisallowedResolution_SendsNoRequest()
    {
        var handler = new ScriptedHandler(_ => Json("{}"));
        var client = CreateClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAttachmentAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 17, 501, 5, 0, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 10, null, null, null, 5, 0, CancellationToken.None));

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Credentials_AreSentAsHeadersAndNeverInTheRequestUrl()
    {
        var handler = new ScriptedHandler(_ => Json("""{ "count": 1 }"""));
        var client = CreateClient(handler);
        const string serviceUrl = "https://gis.example.com/arcgis/rest/services/Private/FeatureServer";

        await client.QueryFeatureCountAsync(serviceUrl, 0, null, 5, 0, CancellationToken.None, TokenCredentials("token-secret"));
        await client.QueryFeatureCountAsync(serviceUrl, 0, null, 5, 0, CancellationToken.None, new GeoservicesCredentialDescriptor
        {
            Mode = GeoservicesAuthenticationModes.Basic,
            Username = "scanner",
            Password = "private-password"
        });

        handler.Requests[0].EsriAuthorization.Should().Be("Bearer token-secret");
        handler.Requests[0].Authorization.Should().BeNull();
        handler.Requests[1].Authorization.Should().Be("Basic c2Nhbm5lcjpwcml2YXRlLXBhc3N3b3Jk");
        handler.Requests[1].EsriAuthorization.Should().BeNull();
        handler.Requests.Should().OnlyContain(r =>
            !r.Uri.ToString().Contains("token", StringComparison.OrdinalIgnoreCase)
            && !r.Uri.ToString().Contains("private-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ErrorEnvelope_IsSanitizedAndNotRetried()
    {
        var handler = new ScriptedHandler(_ => Json("""
            { "error": { "code": 400, "message": "Unable to complete operation.", "details": ["'where' parameter is invalid"] } }
            """));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 10, "BAD(", null, null, 5, 3,
            CancellationToken.None, TokenCredentials("envelope-secret")));

        exception.Message.Should().Be(
            "ArcGIS response error 400 for 'https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer/0/query': "
            + "Unable to complete operation.. Details: 'where' parameter is invalid");
        exception.Message.Should().NotContain("envelope-secret");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(498, nameof(ArcGisAuthenticationFailureKind.CredentialExpired))]
    [InlineData(499, nameof(ArcGisAuthenticationFailureKind.CredentialRequired))]
    [InlineData(403, nameof(ArcGisAuthenticationFailureKind.CredentialDenied))]
    public async Task AuthenticationErrorEnvelope_MapsToCredentialDiagnostics(int code, string expectedKind)
    {
        var handler = new ScriptedHandler(_ => Json($$"""{ "error": { "code": {{code}}, "message": "Token secret-in-body is not valid" } }"""));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ArcGisAuthenticationException>(() => client.QueryObjectIdsAsync(
            "https://gis.example.com/arcgis/rest/services/Private/FeatureServer", 0, null, 5, 2, CancellationToken.None,
            TokenCredentials("expired-secret")));

        exception.Kind.ToString().Should().Be(expectedKind);
        exception.UpstreamStatusCode.Should().Be(code);
        exception.Message.Should().NotContain("secret");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, nameof(ArcGisAuthenticationFailureKind.CredentialRequired))]
    [InlineData(HttpStatusCode.Unauthorized, true, nameof(ArcGisAuthenticationFailureKind.CredentialDenied))]
    [InlineData(HttpStatusCode.Forbidden, true, nameof(ArcGisAuthenticationFailureKind.CredentialDenied))]
    public async Task AuthenticationStatus_MapsToCredentialDiagnosticsWithoutRetry(
        HttpStatusCode status, bool withCredentials, string expectedKind)
    {
        var handler = new ScriptedHandler(_ => Json("""{ "error": { "message": "denied" } }""", status));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ArcGisAuthenticationException>(() => client.QueryAttachmentsAsync(
            "https://gis.example.com/arcgis/rest/services/Private/FeatureServer", 0, [1], 5, 2, CancellationToken.None,
            withCredentials ? TokenCredentials("status-secret") : null));

        exception.Kind.ToString().Should().Be(expectedKind);
        exception.UpstreamStatusCode.Should().Be((int)status);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TransientServerError_IsRetriedThenSucceeds()
    {
        var handler = new ScriptedHandler(request => request.Attempt == 1
            ? Json("""{ "error": { "code": 503, "message": "busy" } }""", HttpStatusCode.ServiceUnavailable)
            : Json("""{ "count": 42 }"""));
        var client = CreateClient(handler);

        var count = await client.QueryFeatureCountAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, null, 5, 1, CancellationToken.None);

        count.Should().Be(42);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task TransientServerError_WhenRetriesExhausted_SurfacesSanitizedStatus()
    {
        var handler = new ScriptedHandler(_ => Json("""{ "error": { "code": 502, "message": "upstream body detail" } }""", HttpStatusCode.BadGateway));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryObjectIdsAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, null, 5, 0, CancellationToken.None));

        exception.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        exception.Message.Should().NotContain("upstream body detail");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TooManyRequests_HonoursRetryAfter()
    {
        var handler = new ScriptedHandler(request =>
        {
            if (request.Attempt > 1)
            {
                return Json(PointFeatures);
            }

            var throttled = Json("""{ "error": { "code": 429, "message": "rate limited" } }""", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return throttled;
        });
        var client = CreateClient(handler);
        var stopwatch = Stopwatch.StartNew();

        var result = await client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 2, null, null, null, 10, 1,
            CancellationToken.None);

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1900),
            "the 2 s Retry-After replaces the ~1 s default backoff");
        result.Features.Should().HaveCount(2);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeclaredOversizedBody_FailsWithoutBufferingOrRetry()
    {
        var handler = new ScriptedHandler(_ => new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(MigrationHttpContentReader.DefaultMaxResponseBytes + 1)
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 10, null, null, null, 5, 2,
            CancellationToken.None));

        exception.Message.Should().Be(
            $"Migration source response declares {MigrationHttpContentReader.DefaultMaxResponseBytes + 1} bytes, "
            + $"exceeding the {MigrationHttpContentReader.DefaultMaxResponseBytes}-byte limit.");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task UndeclaredOversizedBody_StopsReadingAtTheLimit()
    {
        EndlessStream? body = null;
        var handler = new ScriptedHandler(_ =>
        {
            body = new EndlessStream();
            var content = new StreamContent(body);
            content.Headers.ContentLength = null;
            return new CallerOwnedHttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryObjectIdsAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, null, 30, 0, CancellationToken.None));

        exception.Message.Should().Be($"Migration source response exceeded the {MigrationHttpContentReader.DefaultMaxResponseBytes}-byte limit.");
        body!.BytesRead.Should().BeLessThanOrEqualTo(MigrationHttpContentReader.DefaultMaxResponseBytes + (64 * 1024));
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Json("{}");
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryFeaturesAsync(
            "https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer", 0, 0, 10, null, null, null, 5, 3,
            cancellation.Token));

        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("https://gis.example.com/arcgis/rest/services/Inspections/FeatureServer/0/query")]
    // An ArcGIS service root names its service before the type segment; a bare host-level
    // FeatureServer has no service id the SDK can address (honua-sdk-dotnet#381).
    [InlineData("https://gis.example.com/FeatureServer")]
    public async Task NonServiceRootUrl_IsRejectedWithoutRequest(string serviceUrl)
    {
        var handler = new ScriptedHandler(_ => Json("{}"));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryFeatureCountAsync(
            serviceUrl, 0, null, 5, 0, CancellationToken.None));

        exception.Message.Should().Contain("service root");
        handler.Requests.Should().BeEmpty();
    }

    private static ArcGisRestClient CreateClient(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
        => new(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            resolver ?? ((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));

    private static GeoservicesCredentialDescriptor TokenCredentials(string token) => new()
    {
        Mode = GeoservicesAuthenticationModes.Token,
        AccessToken = token
    };

    private static CallerOwnedHttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new CallerOwnedHttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseQuery(Uri uri) => ParseForm(uri.Query.TrimStart('?'));

    private static Dictionary<string, string> ParseForm(string encoded)
        => encoded.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                static parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? EsriAuthorization, string? Body);

    private sealed class ScriptedHandler(Func<ScriptedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            int attempt;
            lock (_requests)
            {
                _requests.Add(new RecordedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Esri-Authorization", out var values) ? values.Single() : null,
                    body));
                attempt = _requests.Count;
            }

            // HttpClient owns and disposes the returned response.
            return respond(new ScriptedRequest(request.RequestUri!, attempt));
        }
    }

    private sealed record ScriptedRequest(Uri RequestUri, int Attempt);

    private sealed class DeclaredLengthContent(long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("An oversized declared body must not be read.");

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)' ');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
