// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerAccessFilteringTests
{
    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/getEstimates")]
    public async Task ServiceGetEstimates_WithHiddenLayer_ReturnsAccessibleLayersOnly()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/getEstimates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var layers = document.RootElement.GetProperty("layers");

        layers.ValueKind.Should().Be(JsonValueKind.Array);
        layers.GetArrayLength().Should().Be(1);
        layers[0].GetProperty("id").GetInt32().Should().Be(ServiceRbacTestFixture.AlphaLayerId);
    }

    /// <summary>
    /// #4386: the disclosure question this fixture exists to answer, finally asked.
    /// <para>
    /// The three sibling tests above prove <b>metadata</b> filtering for the role-gated hidden
    /// layer — it is absent from <c>getEstimates</c>, its domains are filtered, relationships to
    /// it are hidden. None of them ever issued
    /// <c>GET /rest/services/{svc}/FeatureServer/{hiddenLayerId}/query</c> as the <c>reader</c>
    /// principal, so nothing proved that the layer's <i>rows</i> are refused rather than merely
    /// its listing suppressed.
    /// </para>
    /// <para>
    /// The entitled read runs first and is asserted to return the seeded rows and their markers.
    /// That is the positive control: it establishes that this route, on this layer, with this
    /// data, does return records — so the denial below measures the authorization decision and
    /// not an empty layer, an unroutable path or a broken query.
    /// </para>
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_HiddenLayerAsReader_ReturnsZeroRecordsWhileTheEntitledRoleReadsThem()
    {
        const string firstMarker = "hidden-audit-row-8601";
        const string secondMarker = "hidden-audit-row-8602";

        var serverLog = new CapturingLoggerProvider();

        // TestFeatureStore is registered per-scope and keeps its rows in an instance field, so a
        // row written through the root provider is invisible to every request. Pin one instance
        // for this host, and seed the hidden layer through it, so the rows the denial refuses are
        // the rows the query would actually read.
        using var featureStore = new TestFeatureStore();
        using var factory = CreateFactory(serverLog, featureStore);

        foreach (var (auditId, marker) in new[] { (8601, firstMarker), (8602, secondMarker) })
        {
            await featureStore.CreateAsync(
                ServiceRbacTestFixture.BetaLayerId,
                Feature.Create(
                    auditId,
                    null,
                    ImmutableDictionary<string, object?>.Empty
                        .Add("objectid", auditId)
                        .Add("audit_id", auditId)
                        .Add("hidden_status", marker)),
                CancellationToken.None);
        }

        var query =
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/"
            + $"{ServiceRbacTestFixture.BetaLayerId}/query?f=json&where=1%3D1&outFields=*&returnGeometry=false";

        // ---- positive control: the entitled principal reads the rows ----------------
        using (var entitled = ServiceRbacTestFixture.CreateClient(factory, "reader", "hidden-reader"))
        using (var allowed = await entitled.GetAsync(query))
        {
            var body = await allowed.Content.ReadAsStringAsync();
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, serverLog.Describe(body));

            ReadFeatureCount(body).Should().Be(
                2,
                "the hidden layer really does hold the two seeded rows on this route; {0}",
                serverLog.Describe(body));
            body.Should().Contain(firstMarker);
            body.Should().Contain(secondMarker);
        }

        // ---- the denied principal, same query, same rows ----------------------------
        using (var reader = ServiceRbacTestFixture.CreateClient(factory, "reader"))
        using (var denied = await reader.GetAsync(query))
        {
            var body = await denied.Content.ReadAsStringAsync();
            await denied.AssertGeoServicesErrorAsync((int)HttpStatusCode.Forbidden);

            ReadFeatureCount(body).Should().Be(
                0,
                "a principal without 'hidden-reader' must receive no record from the hidden layer; {0}",
                serverLog.Describe(body));
            body.Should().NotContain(firstMarker);
            body.Should().NotContain(secondMarker);
        }
    }

    /// <summary>
    /// The number of records a FeatureServer <c>query</c> body actually carries. A body that is
    /// not a success-shaped payload carries none, which is what the denial case asserts.
    /// </summary>
    private static int ReadFeatureCount(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return 0;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return 0;
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("features", out var features)
                && features.ValueKind == JsonValueKind.Array
                ? features.GetArrayLength()
                : 0;
        }
    }

    /// <summary>
    /// Captures the host's warning-and-above log records so a failing assertion reports the
    /// server-side cause rather than only the generic GeoServices error envelope.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _records = [];

        public string Describe(string body)
        {
            lock (_records)
            {
                return _records.Count == 0
                    ? $"body: {body}"
                    : $"body: {body}\nserver log:\n{string.Join("\n", _records)}";
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var line = $"{logLevel} {category}: {formatter(state, exception)}"
                    + (exception is null ? string.Empty : $" -> {exception}");
                lock (owner._records)
                {
                    owner._records.Add(line);
                }
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryDomains")]
    public async Task QueryDomains_UsesSchemaDomainsAndFiltersHiddenLayers()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/queryDomains?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var domains = document.RootElement.GetProperty("domains");

        domains.ValueKind.Should().Be(JsonValueKind.Array);
        domains.GetArrayLength().Should().Be(1);

        var domain = domains[0];
        domain.GetProperty("layerId").GetInt32().Should().Be(ServiceRbacTestFixture.AlphaLayerId);
        domain.GetProperty("fieldName").GetString().Should().Be("status");
        domain.GetProperty("codedValues").GetArrayLength().Should().Be(2);

        domains.EnumerateArray()
            .Select(item => item.GetProperty("fieldName").GetString())
            .Should()
            .NotContain("is_active");
        domains.EnumerateArray()
            .Select(item => item.GetProperty("layerId").GetInt32())
            .Should()
            .NotContain(ServiceRbacTestFixture.BetaLayerId);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    public async Task QueryRelationships_HidesRelationshipsToHiddenLayers()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/relationships?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var relationships = document.RootElement.GetProperty("relationships");

        relationships.ValueKind.Should().Be(JsonValueKind.Array);
        relationships.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WhenServiceDoesNotAdvertiseSync_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["f"] = "json",
                ["replicaName"] = "sync-disabled",
                ["layers"] = "0",
                ["syncModel"] = "perReplica"
            });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            content);

        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sync capability");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_WithAttachmentSurface_DoesNotAdvertiseUnsupportedUploadId()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var layerResponse = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}?f=json");
        layerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var layerDocument = JsonDocument.Parse(await layerResponse.Content.ReadAsStringAsync());
        var root = layerDocument.RootElement;
        // The Esri "Uploads" service capability is intentionally NOT advertised: it promises
        // the chunked item-upload protocol (uploads/register, etc.) which is not implemented.
        // Attachment support is signaled via hasAttachments + the supports* flags below.
        root.GetProperty("capabilities").GetString().Should().NotContain("Uploads");
        root.GetProperty("hasAttachments").GetBoolean().Should().BeTrue();

        // Regression for #1453: when a layer exposes attachments it must also advertise
        // the attachment-capability flags Esri clients inspect. The ArcGIS Maps SDK for
        // JavaScript gates queryAttachments(where) on supportsQueryAttachments and refuses
        // to issue the request when it is absent/false.
        root.GetProperty("supportsQueryAttachments").GetBoolean().Should().BeTrue();
        root.GetProperty("supportsAttachmentKeywords").GetBoolean().Should().BeTrue();
        root.GetProperty("supportsAttachmentsByUploadId").GetBoolean().Should().BeFalse();

        // Regression for the operations-block inconsistency: the @arcgis/core JS SDK
        // reads supportsQueryAttachments off the nested advancedQueryCapabilities
        // (operations) block, not the root, when deciding whether queryAttachments({where})
        // is permitted. The nested flag must match the root flag, otherwise a layer that
        // advertises attachments at the root is refused the operation.
        root.GetProperty("advancedQueryCapabilities")
            .GetProperty("supportsQueryAttachments")
            .GetBoolean()
            .Should().BeTrue();
    }

    private static WebApplicationFactory<Program> CreateFactory()
        => ServiceRbacTestFixture.CreateFactory(
            static () => new FeatureServerAccessFilteringCatalog(),
            static services => services.AddSingleton<IAttachmentStore, TestAttachmentStore>());

    private static WebApplicationFactory<Program> CreateFactory(
        ILoggerProvider serverLog,
        TestFeatureStore featureStore)
        => ServiceRbacTestFixture.CreateFactory(
            static () => new FeatureServerAccessFilteringCatalog(),
            services =>
            {
                services.AddSingleton<IAttachmentStore, TestAttachmentStore>();
                services.AddSingleton(serverLog);

                // One store for the whole host, so rows seeded by the test are the rows every
                // request reads. These registrations run in ConfigureTestServices and therefore
                // supersede the per-scope defaults.
                services.AddSingleton(featureStore);
                services.AddSingleton<IFeatureReader>(featureStore);
                services.AddSingleton<IFeatureWriter>(featureStore);

                // The RBAC fixture registers no data-source provider, and only the Postgres
                // provider registers ICrsDetectionService — a mandatory scoped dependency of
                // FeatureServerSpatialReferenceResolver. Without it the `query` route fails DI
                // activation and answers 500 before any authorization decision is reachable,
                // which is exactly what the sibling metadata-only tests never noticed. This is
                // the same capability-scoped stub the DuckDB and MySQL providers register.
                services.TryAddScoped<ICrsDetectionService, NoOpCrsDetectionService>();
            });
}

/// <summary>
/// Builds a Metadata v2 graph with one reader-accessible layer and one role-gated hidden layer
/// (both attachment-enabled, each carrying a coded-value domain) so the FeatureServer
/// access-filtering tests can assert hidden layers/domains are filtered for unprivileged callers.
/// </summary>
internal sealed class FeatureServerAccessFilteringCatalog : ITestMetadataV2GraphSource
{
    private static readonly string[] SupportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] Capabilities = ["Query", "Extract"];

    public TestMetadataV2GraphProvider BuildProvider()
    {
        var hiddenLayerPolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["hidden-reader"]);
        var servicePolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["reader"]);

        const string visibleResourceId = "res-layer-0";
        const string hiddenResourceId = "res-layer-1";
        const string serviceId = "svc-alpha";
        var attachmentAnnotation = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["honua.io/attachments"] = bool.TrueString
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                visibleResourceId,
                "Visible Audit Layer",
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Length = 255, Description = "Name" },
                    new MetadataV2Field
                    {
                        Name = "status",
                        Type = MetadataV2FieldType.String,
                        Nullable = true,
                        Length = 32,
                        Description = "Status",
                        Domain = CodedDomain("AuditStatus", ("open", "Open"), ("closed", "Closed"))
                    },
                    new MetadataV2Field { Name = "is_active", Type = MetadataV2FieldType.Boolean, Nullable = true, Description = "Active" }
                ],
                annotations: attachmentAnnotation)
            .AddStorageBinding("binding-layer-0", visibleResourceId, "test.layers.0", storageLayerId: ServiceRbacTestFixture.AlphaLayerId)
            .AddResource(
                hiddenResourceId,
                "Hidden Audit Layer",
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                    new MetadataV2Field { Name = "audit_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Audit ID" },
                    new MetadataV2Field
                    {
                        Name = "hidden_status",
                        Type = MetadataV2FieldType.String,
                        Nullable = true,
                        Length = 32,
                        Description = "Hidden Status",
                        Domain = CodedDomain("HiddenStatus", ("internal", "Internal"), ("sealed", "Sealed"))
                    }
                ],
                accessPolicy: hiddenLayerPolicy,
                annotations: attachmentAnnotation)
            .AddStorageBinding("binding-layer-1", hiddenResourceId, "test.layers.1", storageLayerId: ServiceRbacTestFixture.BetaLayerId)
            .AddService(
                serviceId,
                ServiceRbacTestFixture.AlphaService,
                protocols: MetadataV2ServiceProtocols.All,
                accessPolicy: servicePolicy,
                options: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["capabilities"] = JsonSerializer.SerializeToElement(Capabilities),
                    ["supportedFormats"] = JsonSerializer.SerializeToElement(SupportedFormats)
                })
            .AddPublication(
                "svc-alpha-layer-0",
                serviceId,
                visibleResourceId,
                layerIndex: ServiceRbacTestFixture.AlphaLayerId,
                storageBindingId: "binding-layer-0",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .AddPublication(
                "svc-alpha-layer-1",
                serviceId,
                hiddenResourceId,
                layerIndex: ServiceRbacTestFixture.BetaLayerId,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .BuildProvider();
    }

    private static MetadataV2FieldDomain CodedDomain(string name, params (string Code, string Label)[] values)
        => new()
        {
            Name = name,
            Type = "codedValue",
            CodedValues = values
                .Select(value => new MetadataV2CodedValue
                {
                    Code = JsonSerializer.SerializeToElement(value.Code),
                    Name = value.Label
                })
                .ToArray()
        };
}
