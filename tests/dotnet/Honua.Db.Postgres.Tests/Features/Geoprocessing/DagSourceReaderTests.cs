// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Geoprocessing;

/// <summary>
/// In-memory unit coverage for the first-class DAG remote source connectors. Each
/// reader is exercised against a faked transport (HttpMessageHandler / streaming
/// feature store) so the upstream-query build, pagination/streaming, and GeoJSON
/// projection are verified without Docker or a live remote service.
/// </summary>
public sealed class DagSourceReaderTests
{
    // -------------------------------------------------------------------------
    // source.esri-featureserver — wraps ArcGisRestClient paging + Esri->GeoJSON
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task EsriFeatureServer_PaginatesUntilTransferLimitClears_AndConvertsGeometry()
    {
        var page1 = """
        {"features":[
          {"attributes":{"OBJECTID":1,"name":"a"},"geometry":{"x":10.0,"y":20.0}},
          {"attributes":{"OBJECTID":2,"name":"b"},"geometry":{"x":30.0,"y":40.0}}
        ],"exceededTransferLimit":true,"spatialReference":{"wkid":4326}}
        """;
        var page2 = """
        {"features":[
          {"attributes":{"OBJECTID":3,"name":"c"},"geometry":{"x":50.0,"y":60.0}}
        ],"exceededTransferLimit":false,"spatialReference":{"wkid":4326}}
        """;
        var page3Empty = """{"features":[],"exceededTransferLimit":false}""";

        var capturedUrls = new List<string>();
        var handler = new SequencedJsonHandler(capturedUrls, page1, page2, page3Empty);
        var reader = new EsriFeatureServerDagSource(
            BuildArcGisClient(handler),
            NullLogger<EsriFeatureServerDagSource>.Instance);

        var request = new DagSourceRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Sample/FeatureServer",
            EsriLayerId = 0,
            Where = "status = 'active'",
            PageSize = 2,
            OutputSrid = 4326
        };

        var features = await CollectAsync(reader.ReadAsync(request));

        features.Should().HaveCount(3);
        // Geometry round-trips to GeoJSON points through the reused Esri->GeoJSON converter.
        var firstGeometry = new GeoJsonReader().Read<Geometry>(features[0].GeometryGeoJson!) as Point;
        var point = firstGeometry ?? throw new InvalidOperationException("Expected a Point geometry.");
        point.X.Should().BeApproximately(10.0, 1e-9);
        point.Y.Should().BeApproximately(20.0, 1e-9);
        features[0].Attributes["name"].Should().Be("a");

        // The where clause + paging offsets were pushed into the upstream query URL.
        capturedUrls.Should().Contain(u => u.Contains("where=status", StringComparison.Ordinal));
        capturedUrls.Should().Contain(u => u.Contains("resultOffset=0", StringComparison.Ordinal));
        capturedUrls.Should().Contain(u => u.Contains("resultOffset=2", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task EsriFeatureServer_SinceWatermark_AddsTemporalPredicate()
    {
        var page = """{"features":[],"exceededTransferLimit":false}""";
        var capturedUrls = new List<string>();
        var handler = new SequencedJsonHandler(capturedUrls, page);
        var reader = new EsriFeatureServerDagSource(
            BuildArcGisClient(handler),
            NullLogger<EsriFeatureServerDagSource>.Instance);

        var request = new DagSourceRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Sample/FeatureServer",
            Since = "2026-01-01T00:00:00Z",
            WatermarkField = "last_edited_date"
        };

        _ = await CollectAsync(reader.ReadAsync(request));

        capturedUrls.Should().ContainSingle()
            .Which.Should().Contain("last_edited_date");
    }

    // -------------------------------------------------------------------------
    // source.ogc-features — link-based pagination + bbox/filter query build
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task OgcFeatures_FollowsNextLinks_AndPushesBboxAndFilter()
    {
        var page1 = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"id":1}}
        ],"links":[{"rel":"next","href":"https://example.com/page2"}]}
        """;
        var page2 = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"id":2}}
        ],"links":[]}
        """;

        var capturedUrls = new List<string>();
        var handler = new SequencedJsonHandler(capturedUrls, page1, page2);
        var reader = new OgcFeaturesDagSource(new HttpClient(handler), NullLogger<OgcFeaturesDagSource>.Instance);

        var request = new DagSourceRequest
        {
            ServiceUrl = "https://example.com/ogc",
            CollectionId = "buildings",
            Bbox = "0,0,10,10",
            Where = "height>5",
            PageSize = 1
        };

        var features = await CollectAsync(reader.ReadAsync(request));

        features.Should().HaveCount(2);
        features[1].Attributes["id"].Should().Be(2L);
        capturedUrls[0].Should().Contain("bbox=0");
        capturedUrls[0].Should().Contain("filter=");
        capturedUrls[0].Should().Contain("limit=1");
        capturedUrls[1].Should().Be("https://example.com/page2");
    }

    // -------------------------------------------------------------------------
    // source.wfs — startIndex/count paging, terminate on empty page
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task Wfs_PagesByStartIndex_UntilEmptyPage()
    {
        var page1 = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,1]},"properties":{"gid":1}}
        ]}
        """;
        var page2 = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2,2]},"properties":{"gid":2}}
        ]}
        """;
        var empty = """{"type":"FeatureCollection","features":[]}""";

        var capturedUrls = new List<string>();
        var handler = new SequencedJsonHandler(capturedUrls, page1, page2, empty);
        var reader = new WfsDagSource(new HttpClient(handler), NullLogger<WfsDagSource>.Instance);

        var request = new DagSourceRequest
        {
            ServiceUrl = "https://example.com/wfs",
            CollectionId = "topp:states",
            PageSize = 1
        };

        var features = await CollectAsync(reader.ReadAsync(request));

        features.Should().HaveCount(2);
        capturedUrls[0].Should().Contain("startIndex=0");
        capturedUrls[0].Should().Contain("typeNames=topp");
        capturedUrls[1].Should().Contain("startIndex=1");
    }

    [UnitTest]
    public async Task Wfs_StopsWhenServerIgnoresStartIndex()
    {
        // Same page returned regardless of startIndex => the reader must stop after the
        // repeated-first-feature is detected, not stream unbounded duplicates.
        var samePage = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,1]},"properties":{"gid":1}}
        ]}
        """;

        var handler = new RepeatingJsonHandler(samePage);
        var reader = new WfsDagSource(new HttpClient(handler), NullLogger<WfsDagSource>.Instance);

        var request = new DagSourceRequest
        {
            ServiceUrl = "https://example.com/wfs",
            CollectionId = "topp:states",
            PageSize = 1
        };

        var features = await CollectAsync(reader.ReadAsync(request));

        // First page yields 1; second page is detected as a repeat and stops the stream.
        features.Should().HaveCount(1);
    }

    // -------------------------------------------------------------------------
    // source.honua-layer — wraps the canonical streaming feature store
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task HonuaLayer_StreamsFromCatalog_AndBuildsBboxSpatialFilter()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var wkb = new WKBWriter().Write(factory.CreatePoint(new Coordinate(5, 6)));
        var stored = new Feature
        {
            Id = 1,
            Geometry = wkb,
            Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "x")
        };

        FeatureQuery? capturedQuery = null;
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(stored));

        var reader = new HonuaLayerDagSource(store);
        var request = new DagSourceRequest
        {
            LayerId = 42,
            Where = "name = 'x'",
            Bbox = "0,0,10,10",
            OutputSrid = 4326
        };

        var features = await CollectAsync(reader.ReadAsync(request));

        features.Should().HaveCount(1);
        var geometry = new GeoJsonReader().Read<Geometry>(features[0].GeometryGeoJson!) as Point;
        geometry!.X.Should().BeApproximately(5, 1e-9);
        features[0].Attributes["name"].Should().Be("x");

        capturedQuery.Should().NotBeNull();
        var query = capturedQuery!.Value;
        query.Where.Should().Be("name = 'x'");
        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Value.IsSimpleEnvelope.Should().BeTrue();
    }

    [UnitTest]
    public async Task HonuaLayer_ObjectIdsRequest_BuildsExactObjectIdSet()
    {
        // #4624: analytics.buffer-aggregate (and its layer-sourced siblings) advertise
        // 'objectIds' but source.honua-layer previously never propagated it into the
        // FeatureQuery the streaming store filters by, so it was silently accepted and
        // ignored. Assert the built FeatureQuery.ObjectIds is EXACTLY the requested set
        // (order preserved, no extras, no drops) — an independent value check, not a
        // snapshot of whatever the reader happened to produce before this fix.
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync());

        FeatureQuery? capturedQuery = null;
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync());

        var reader = new HonuaLayerDagSource(store);
        var request = new DagSourceRequest { LayerId = 42, ObjectIds = "5, 10, 15" };

        await CollectAsync(reader.ReadAsync(request));

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.ObjectIds.Should().NotBeNull();
        capturedQuery.Value.ObjectIds!.Value.Should().Equal(5L, 10L, 15L);
    }

    [UnitTest]
    public async Task HonuaLayer_GeometryOrTimeSelector_WithoutCanonicalTranslator_FailsClosedBeforeReading()
    {
        // #4624: a geometry/time selector the connector cannot interpret canonically must never be
        // dropped — reading without it would hand back a broader feature set than requested.
        var store = Substitute.For<IStreamingFeatureStore>();
        var reader = new HonuaLayerDagSource(store, SingleLayerMetadata(42));

        foreach (var request in new[]
        {
            new DagSourceRequest { LayerId = 42, Geometry = """{"xmin":0,"ymin":0,"xmax":1,"ymax":1}""", GeometryType = "esriGeometryEnvelope" },
            new DagSourceRequest { LayerId = 42, Time = "1700000000000" },
        })
        {
            var act = async () => await CollectAsync(reader.ReadAsync(request));
            await act.Should().ThrowAsync<DagSourceSelectionException>();
        }

        store.DidNotReceive().StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HonuaLayer_CanonicalSelectors_AreTranslatedOnceAndLeaveAuthorizationToTheStore()
    {
        var translated = new FeatureQuery
        {
            Where = "pop < 500",
            SqlFilter = new SqlFragment("(pop < @p0) AND (observed >= @p1)", [500, DateTimeOffset.UnixEpoch]),
            ObjectIds = ImmutableArray.Create(1L, 2L),
            SpatialFilter = SpatialFilter.Create([1, 2, 3], SpatialRelationship.Within, 3857, isSimpleEnvelope: false),
        };
        LayerSelectionFilter? seen = null;
        var translator = Substitute.For<ILayerSelectionFilterTranslator>();
        translator.TranslateAsync(Arg.Do<LayerSelectionFilter>(s => seen = s), Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(LayerSelectionTranslation.Success(translated));

        FeatureQuery? captured = null;
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync());

        var reader = new HonuaLayerDagSource(store, SingleLayerMetadata(42), translator);
        await CollectAsync(reader.ReadAsync(new DagSourceRequest
        {
            LayerId = 42,
            Where = "pop < 500",
            ObjectIds = "1,2",
            Geometry = """{"rings":[[[0,0],[0,1],[1,1],[0,0]]]}""",
            GeometryType = "esriGeometryPolygon",
            InSr = "3857",
            SpatialRel = "esriSpatialRelWithin",
            Time = "2026-01-01T00:00:00Z,2026-01-31T00:00:00Z",
            TimeRelation = "esriTimeRelationOverlaps",
        }));

        seen.Should().BeEquivalentTo(new LayerSelectionFilter
        {
            Where = "pop < 500",
            ObjectIds = "1,2",
            Geometry = """{"rings":[[[0,0],[0,1],[1,1],[0,0]]]}""",
            GeometryType = "esriGeometryPolygon",
            InSr = "3857",
            SpatialRel = "esriSpatialRelWithin",
            Time = "2026-01-01T00:00:00Z,2026-01-31T00:00:00Z",
            TimeRelation = "esriTimeRelationOverlaps",
        });
        captured.Should().NotBeNull();
        var query = captured!.Value;
        query.SqlFilter.Should().BeSameAs(translated.SqlFilter, "the translated where+time predicate is what the store filters by");
        query.SpatialFilter.Should().Be(translated.SpatialFilter);
        query.ObjectIds!.Value.Should().Equal(1L, 2L);
        query.EnforcedSqlFilter.Should().BeNull(
            "permanent filters and row-level security are resolved and ANDed by the store itself, never replaced by caller selectors");
        query.EnforcedMaskedFields.Should().BeNull();
        query.SpatialReferenceSrid.Should().Be(4326, "the connector keeps the canonical storage SRID");
    }

    [UnitTest]
    public async Task HonuaLayer_TranslatorRejectsSelector_SurfacesCallerFacingErrorWithoutReading()
    {
        var translator = Substitute.For<ILayerSelectionFilterTranslator>();
        translator.TranslateAsync(Arg.Any<LayerSelectionFilter>(), Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(LayerSelectionTranslation.Failure(
                "Invalid parameter",
                "The time parameter is not supported: this layer is not time-aware (no timeInfo / temporal fields)."));
        var store = Substitute.For<IStreamingFeatureStore>();
        var reader = new HonuaLayerDagSource(store, SingleLayerMetadata(42), translator);

        var act = async () => await CollectAsync(reader.ReadAsync(new DagSourceRequest { LayerId = 42, Time = "1700000000000" }));

        await act.Should().ThrowAsync<DagSourceSelectionException>().WithMessage("Invalid parameter: *not time-aware*");
        store.DidNotReceive().StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    private static FixedMetadataV2GraphProvider SingleLayerMetadata(int layerId)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"res-{layerId}", Name = $"layer-{layerId}" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Spatial = new MetadataV2ResourceSpatial { StorageCrs = new MetadataV2SpatialReference { Srid = 4326 } },
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{layerId}", Name = $"binding-{layerId}" },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"public.layer_{layerId}",
            StorageLayerId = layerId,
        };
        return new FixedMetadataV2GraphProvider(new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = 1, Resources = [resource], StorageBindings = [binding] },
            "\"selection-tests\"",
            DateTimeOffset.UnixEpoch));
    }

    private sealed class FixedMetadataV2GraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == snapshot.Revision ? snapshot : null);
    }

    [UnitTest]
    public async Task HonuaLayer_NoObjectIdsRequest_LeavesObjectIdsUnset()
    {
        var store = Substitute.For<IStreamingFeatureStore>();
        FeatureQuery? capturedQuery = null;
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync());

        var reader = new HonuaLayerDagSource(store);
        await CollectAsync(reader.ReadAsync(new DagSourceRequest { LayerId = 42 }));

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.ObjectIds.Should().BeNull("no objectIds filter was requested");
    }

    [UnitTest]
    public async Task HonuaLayer_InsideJobScopeWithNoSubmitterSnapshot_RefusesTheRead()
    {
        // honua-server#3068 fail-closed contract. An active job scope carrying NO submitter
        // snapshot means the read cannot be constrained to the caller: the request-scoped RLS
        // and field-mask sources would resolve "nothing applies" and hand back every row and
        // every attribute. The connector must refuse instead — and must refuse BEFORE touching
        // the store, so no rows are ever read.
        var store = Substitute.For<IStreamingFeatureStore>();
        var reader = new HonuaLayerDagSource(store);

        using var scope = JobSecurityScope.Begin(submitter: null);

        var act = async () => await CollectAsync(reader.ReadAsync(new DagSourceRequest { LayerId = 42 }));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _ = store.DidNotReceive().StreamFeaturesAsync(
            Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HonuaLayer_InsideJobScopeWithSubmitterSnapshot_ReadsNormally()
    {
        // Positive control: a job that DID capture its submitter reads exactly as before, so the
        // guard above is a genuine fail-closed decision rather than a blanket refusal of
        // background layer reads.
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var wkb = new WKBWriter().Write(factory.CreatePoint(new Coordinate(1, 2)));
        var stored = new Feature
        {
            Id = 1,
            Geometry = wkb,
            Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "ok")
        };

        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(stored));

        var reader = new HonuaLayerDagSource(store);

        using var scope = JobSecurityScope.Begin(
            new JobSecurityContext("submitter", null, [new JobSecurityClaim("category", "test")]));

        var features = await CollectAsync(reader.ReadAsync(new DagSourceRequest { LayerId = 42 }));

        features.Should().HaveCount(1);
        features[0].Attributes["name"].Should().Be("ok");
    }

    // -------------------------------------------------------------------------
    // GeoJSON page projection (shared by OGC/WFS)
    // -------------------------------------------------------------------------

    [UnitTest]
    public void GeoJsonPageReader_ParsesNumberMatchedAndNextLink()
    {
        var body = """
        {"type":"FeatureCollection","numberMatched":99,"features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"a":1}}
        ],"links":[{"rel":"next","href":"https://example.com/n"}]}
        """;

        var page = GeoJsonPageReader.ParsePage(body);

        page.Features.Should().HaveCount(1);
        page.NumberMatched.Should().Be(99);
        page.NextLink.Should().Be("https://example.com/n");
        page.Features[0].Attributes["a"].Should().Be(1L);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ArcGisRestClient BuildArcGisClient(HttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            // Resolve any host to a public address so the SSRF guard admits the test host.
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));

    private static async Task<List<DagSourceFeature>> CollectAsync(IAsyncEnumerable<DagSourceFeature> stream)
    {
        var result = new List<DagSourceFeature>();
        await foreach (var item in stream)
        {
            result.Add(item);
        }

        return result;
    }

    private static async IAsyncEnumerable<Feature> ToAsync(params Feature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }

    private sealed class SequencedJsonHandler : HttpMessageHandler
    {
        private readonly List<string> _capturedUrls;
        private readonly Queue<string> _bodies;

        public SequencedJsonHandler(List<string> capturedUrls, params string[] bodies)
        {
            _capturedUrls = capturedUrls;
            _bodies = new Queue<string>(bodies);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _capturedUrls.Add(request.RequestUri!.ToString());
            var body = _bodies.Count > 0 ? _bodies.Dequeue() : """{"features":[]}""";
            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that
            // invokes this handler; it is disposed by the caller, not here (cs/local-not-disposed
            // false positive).
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RepeatingJsonHandler : HttpMessageHandler
    {
        private readonly string _body;

        public RepeatingJsonHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that
            // invokes this handler; it is disposed by the caller, not here (cs/local-not-disposed
            // false positive).
            => Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
