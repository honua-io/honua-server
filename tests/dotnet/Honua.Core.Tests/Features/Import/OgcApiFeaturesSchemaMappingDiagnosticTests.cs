// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Slice 4 — schema-mapping diagnostics. Covers each classification (assisted, manual-review,
/// unsupported, clean) plus a JSON round-trip through <see cref="ImportJsonContextProxy"/> to lock
/// the wire shape consumed by the OGC API Features import endpoint.
/// </summary>
public sealed partial class OgcApiFeaturesSchemaMappingDiagnosticTests
{
    [Fact]
    public async Task ImportCollectionAsync_WithCompatibleSchema_EmitsNoMappingDiagnostics()
    {
        var handler = new SchemaFixtureHandler(includeSchema: true);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new IntrospectableSink(new[]
        {
            new OgcApiFeaturesSinkColumn { Name = "name", DataType = "text" },
            new OgcApiFeaturesSinkColumn { Name = "speed_limit", DataType = "integer" }
        });

        var service = CreateService(httpClient, sink);
        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.MappingDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportCollectionAsync_WithIntToBigintWidening_EmitsAssistedDiagnostic()
    {
        var handler = new SchemaFixtureHandler(includeSchema: true);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new IntrospectableSink(new[]
        {
            new OgcApiFeaturesSinkColumn { Name = "name", DataType = "text" },
            new OgcApiFeaturesSinkColumn { Name = "speed_limit", DataType = "bigint" }
        });

        var service = CreateService(httpClient, sink);
        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        var diagnostic = result.MappingDiagnostics.Should()
            .ContainSingle(d => d.PropertyName == "speed_limit").Subject;
        diagnostic.Classification.Should().Be(OgcApiFeaturesSchemaMappingClassification.Assisted);
        diagnostic.Severity.Should().Be("info");
        diagnostic.SourceType.Should().Be("integer");
        diagnostic.TargetColumnType.Should().Be("bigint");
    }

    [Fact]
    public async Task ImportCollectionAsync_WithVarcharNarrowing_EmitsManualReviewDiagnostic()
    {
        var handler = new SchemaFixtureHandler(includeSchema: true);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        // Source schema declares name as `string` (unbounded text); target is varchar(32).
        var sink = new IntrospectableSink(new[]
        {
            new OgcApiFeaturesSinkColumn { Name = "name", DataType = "varchar(32)" },
            new OgcApiFeaturesSinkColumn { Name = "speed_limit", DataType = "integer" }
        });

        var service = CreateService(httpClient, sink);
        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        var diagnostic = result.MappingDiagnostics.Should()
            .ContainSingle(d => d.PropertyName == "name").Subject;
        diagnostic.Classification.Should().Be(OgcApiFeaturesSchemaMappingClassification.ManualReview);
        diagnostic.Severity.Should().Be("warning");
        diagnostic.TargetColumnType.Should().Be("varchar(32)");
        result.Warnings.Should().Contain(w => w.Contains("Schema mapping manualreview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportCollectionAsync_WithUnknownProperty_EmitsUnsupportedDiagnostic()
    {
        var handler = new SchemaFixtureHandler(includeSchema: true);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        // Target only has `name`; source advertises `name` + `speed_limit`. The latter has no
        // matching column → unsupported.
        var sink = new IntrospectableSink(new[]
        {
            new OgcApiFeaturesSinkColumn { Name = "name", DataType = "text" }
        });

        var service = CreateService(httpClient, sink);
        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        var diagnostic = result.MappingDiagnostics.Should()
            .ContainSingle(d => d.PropertyName == "speed_limit").Subject;
        diagnostic.Classification.Should().Be(OgcApiFeaturesSchemaMappingClassification.Unsupported);
        diagnostic.Severity.Should().Be("error");
        diagnostic.TargetColumnType.Should().BeNull();
        diagnostic.Reason.Should().Contain("no matching column");
    }

    [Fact]
    public async Task ImportCollectionAsync_WithoutSchemaEndpoint_InfersFromFirstPageFeatures()
    {
        // includeSchema: false → /schemas/feature returns 404; mapper falls back to inference.
        var handler = new SchemaFixtureHandler(includeSchema: false);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new IntrospectableSink(new[]
        {
            new OgcApiFeaturesSinkColumn { Name = "name", DataType = "text" }
            // speed_limit absent → unsupported by inference.
        });

        var service = CreateService(httpClient, sink);
        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.MappingDiagnostics.Should()
            .Contain(d => d.PropertyName == "speed_limit" &&
                          d.Classification == OgcApiFeaturesSchemaMappingClassification.Unsupported);
    }

    [Fact]
    public async Task ImportCollectionAsync_WithSinkReportingNoColumns_SkipsDiagnostics()
    {
        var handler = new SchemaFixtureHandler(includeSchema: true);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new IntrospectableSink(Array.Empty<OgcApiFeaturesSinkColumn>());

        var service = CreateService(httpClient, sink);
        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.MappingDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void OgcApiFeaturesImportResult_WithMappingDiagnostics_RoundTripsThroughJsonContext()
    {
        var original = new OgcApiFeaturesImportResult
        {
            Success = true,
            CollectionId = "roads",
            Target = "test_schema.roads",
            FeaturesImported = 1,
            PagesFetched = 1,
            Duration = TimeSpan.FromSeconds(3),
            MappingDiagnostics = new[]
            {
                new OgcApiFeaturesSchemaMappingDiagnostic
                {
                    PropertyName = "name",
                    SourceType = "string",
                    TargetColumnType = "varchar(32)",
                    Classification = OgcApiFeaturesSchemaMappingClassification.ManualReview,
                    Severity = "warning",
                    Reason = "narrowing conversion"
                },
                new OgcApiFeaturesSchemaMappingDiagnostic
                {
                    PropertyName = "speed_limit",
                    SourceType = "integer",
                    TargetColumnType = null,
                    Classification = OgcApiFeaturesSchemaMappingClassification.Unsupported,
                    Severity = "error",
                    Reason = "no target column"
                }
            }
        };

        var json = JsonSerializer.Serialize(original, ImportJsonContextProxy.ResultInfo);

        // Sanity: camelCase property + classification serialized as string (via custom converter).
        json.Should().Contain("\"mappingDiagnostics\"");
        json.Should().Contain("\"manualReview\"").And.Contain("\"unsupported\"");

        var roundTripped = JsonSerializer.Deserialize<OgcApiFeaturesImportResult>(json, ImportJsonContextProxy.ResultInfo);

        roundTripped.Should().NotBeNull();
        roundTripped!.MappingDiagnostics.Should().HaveCount(2);
        roundTripped.MappingDiagnostics[0].Classification.Should().Be(OgcApiFeaturesSchemaMappingClassification.ManualReview);
        roundTripped.MappingDiagnostics[1].TargetColumnType.Should().BeNull();
    }

    private static OgcApiFeaturesImportService CreateService(HttpClient httpClient, IOgcApiFeaturesCollectionSink sink)
        => new(
            httpClient,
            sink,
            NullLogger<OgcApiFeaturesImportService>.Instance,
            static (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

    private static OgcApiFeaturesImportRequest NewRequest()
        => new()
        {
            ServiceUrl = "https://demo.example/ogcapi/",
            CollectionId = "roads",
            TargetSchema = "test_schema",
            PageSize = 10,
            TimeoutSeconds = 5
        };

    private sealed class IntrospectableSink : IOgcApiFeaturesCollectionSink
    {
        private readonly IReadOnlyList<OgcApiFeaturesSinkColumn> _columns;

        public IntrospectableSink(IReadOnlyList<OgcApiFeaturesSinkColumn> columns)
        {
            _columns = columns;
        }

        public Task EnsureTargetAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<int> WriteFeaturesAsync(
            OgcApiFeaturesSinkTarget target,
            IReadOnlyList<OgcApiFeaturesSinkFeature> features,
            CancellationToken cancellationToken)
            => Task.FromResult(features.Count);

        public Task<IReadOnlyList<OgcApiFeaturesSinkColumn>> GetTargetColumnsAsync(
            OgcApiFeaturesSinkTarget target,
            CancellationToken cancellationToken)
            => Task.FromResult(_columns);
    }

    private sealed class SchemaFixtureHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> RequestUris { get; } = new();

        private readonly bool _includeSchema;

        public SchemaFixtureHandler(bool includeSchema)
        {
            _includeSchema = includeSchema;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/collections/roads", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "roads",
                      "links": [
                        { "rel": "items", "href": "https://demo.example/ogcapi/collections/roads/items", "type": "application/geo+json" }
                      ]
                    }
                    """, "application/json");
            }

            if (path.EndsWith("/collections/roads/schemas/feature", StringComparison.Ordinal))
            {
                if (!_includeSchema)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Json("""
                    {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "speed_limit": { "type": "integer", "format": "int32" }
                      }
                    }
                    """, "application/schema+json");
            }

            if (path.EndsWith("/items", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "type": "FeatureCollection",
                      "numberMatched": 1,
                      "links": [
                        { "rel": "self", "href": "https://demo.example/ogcapi/collections/roads/items" }
                      ],
                      "features": [
                        {
                          "type": "Feature",
                          "id": "road.1",
                          "geometry": { "type": "Point", "coordinates": [-157.85, 21.30] },
                          "properties": { "name": "King", "speed_limit": 25 }
                        }
                      ]
                    }
                    """, "application/geo+json");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body, string mediaType)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }

    /// <summary>
    /// Local mirror of the server's <c>ImportJsonContext</c>. Mirrors the same
    /// <see cref="JsonSerializerOptions"/> so we can assert wire shape without taking a dependency
    /// on the server assembly from the core test project.
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(OgcApiFeaturesImportResult))]
    [JsonSerializable(typeof(OgcApiFeaturesSchemaMappingDiagnostic))]
    [JsonSerializable(typeof(OgcApiFeaturesSchemaMappingClassification))]
    private sealed partial class ImportJsonContextProxy : JsonSerializerContext
    {
        public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<OgcApiFeaturesImportResult> ResultInfo
            => Default.OgcApiFeaturesImportResult;
    }
}
