// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Proto = Geospatial.V1;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed partial class GrpcIntegrationTests(GrpcIntegrationTests.GrpcHost host)
    : IClassFixture<GrpcIntegrationTests.GrpcHost>, IDisposable
{
    private const long SeededFeatureCount = 5;

    private const long SeededTestCategoryCount = 3;

    private const long SeededSampleCategoryCount = 2;

    private const int StreamBatchSize = 2;

    private const string AddedFeatureName = "gRPC integration test";
    private const double AddedFeatureX = -157.85;
    private const double AddedFeatureY = 21.30;

    private readonly WebAppFixture _fixture = host.Fixture;
    private readonly Proto.FeatureService.FeatureServiceClient _client = host.Client;
    private readonly Metadata _headers = host.Headers;

    // The paired-publication cases add publications to the shared graph; put the seeded
    // ones back so the next case reads the seed.
    public void Dispose()
    {
        if (_fixture.GetCurrentV2GraphSnapshot().Graph.Revision != host.GraphRevision)
        {
            _fixture.MutateV2Graph(graph => graph with { Publications = host.SeededPublications });
            host.GraphRevision = _fixture.GetCurrentV2GraphSnapshot().Graph.Revision;
        }
    }

    // One host per class. The cases read the seeded layer; the one case that adds a row
    // deletes it again.
    public sealed partial class GrpcHost : IAsyncLifetime
    {
        private GrpcChannel? _channel;

        internal WebAppFixture Fixture { get; } = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Community)
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Grpc:StreamBatchSize"] = StreamBatchSize.ToString(CultureInfo.InvariantCulture)
                })));

        internal Proto.FeatureService.FeatureServiceClient Client { get; private set; } = null!;
        internal Metadata Headers { get; } = [];
        internal IReadOnlyList<MetadataV2Layer> SeededPublications { get; private set; } = [];
        internal long GraphRevision { get; set; }

        public async Task InitializeAsync()
        {
            ConfigureAttachmentReadServices();
            await Fixture.InitializeAsync();
            var seeded = Fixture.GetCurrentV2GraphSnapshot().Graph;
            SeededPublications = seeded.Publications;
            GraphRevision = seeded.Revision;

            // GrpcWebHandler converts gRPC to gRPC-Web (HTTP/1.1) for the test server; the
            // server's UseGrpcWeb middleware (DefaultEnabled = true) converts it back.
            var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, Fixture.CreateHandler());
            _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = grpcWebHandler
            });
            Client = new Proto.FeatureService.FeatureServiceClient(_channel);

            // gRPC metadata entries become HTTP headers, so the schema middleware reads the
            // X-Honua-Test-Schema isolation header from them.
            if (Fixture.CurrentSchema is not null)
            {
                Headers.Add("X-Honua-Test-Schema", Fixture.CurrentSchema);
            }
        }

        public async Task DisposeAsync()
        {
            _channel?.Dispose();
            await Fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData("date", "2026-02-30")]
    [InlineData("time", "24:00:00")]
    [InlineData("timestamp_offset", "2026-11-14T01:02:03+14:01")]
    [InlineData("decimal", "1e6")]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_InvalidTypedAttribute_RejectsWholeCallBeforeWrites(string kind, string value)
    {
        var update = new Proto.Feature { Id = 1 };
        update.Attributes["name"] = kind switch
        {
            "date" => new Proto.AttributeValue { DateValue = value },
            "time" => new Proto.AttributeValue { TimeValue = value },
            "timestamp_offset" => new Proto.AttributeValue { TimestampOffsetValue = value },
            _ => new Proto.AttributeValue { DecimalValue = value },
        };
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId
        };
        request.Updates.Add(update);
        var before = await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, 1);

        var act = async () => await _client!.ApplyEditsAsync(request, _headers);
        var failure = await act.Should().ThrowAsync<RpcException>();
        failure.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, 1))
            .Should().Be(before);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId))
            .Should().Be(SeededFeatureCount);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_MalformedGlobalIdSelector_RejectsWholeCall()
    {
        var request = GlobalIdQuery("invalid-global-id");

        await AssertInvalidArgumentAsync(request);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ObjectAndGlobalIdSelectorsTogether_RejectsWholeCall()
    {
        var request = GlobalIdQuery("4e886876-f3d3-4fc1-adfd-765880396ff2");
        request.ObjectIds.Add(1);

        await AssertInvalidArgumentAsync(request);
    }

    private static Proto.QueryFeaturesRequest GlobalIdQuery(string globalId)
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId
        };
        request.GlobalIds.Add(globalId);
        return request;
    }

    private async Task AssertInvalidArgumentAsync(Proto.QueryFeaturesRequest request)
    {
        var act = async () => await _client!.QueryFeaturesAsync(request, _headers);
        var failure = await act.Should().ThrowAsync<RpcException>();
        failure.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_CountOnly_ReturnsCount()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            ReturnCountOnly = true
        };

        var response = await _client!.QueryFeaturesAsync(request, _headers);

        response.Should().NotBeNull();

        // The exact seeded cardinality. A ReturnCountOnly path that returns zero for a
        // populated layer — or ignores ReturnCountOnly and counts nothing — fails here.
        response.Count.Should().Be(SeededFeatureCount,
            "tests/seed/server.yaml publishes exactly {0} rows on the test layer", SeededFeatureCount);
        response.Features.Should().BeEmpty("a count-only query must not buffer feature payloads");
        response.ObjectIdFieldName.Should().Be("objectid");

        // The database agrees, so the count is the layer's real cardinality rather than a
        // number the adapter happened to produce.
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId))
            .Should().Be(SeededFeatureCount);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_CountOnlyWithWhereClause_CountsOnlyMatchingRows()
    {
        // Matching and non-matching rows, each with its own exact count: a server that
        // drops the WHERE clause returns 5 every time and fails two of these three.
        (await CountAsync("category = 'test'")).Should().Be(SeededTestCategoryCount);
        (await CountAsync("category = 'sample'")).Should().Be(SeededSampleCategoryCount);
        (await CountAsync("category = 'no-such-category'")).Should().Be(0);

        // The unfiltered count is the sum of the two partitions, so neither branch can be
        // right by accident.
        (await CountAsync(where: null)).Should().Be(SeededTestCategoryCount + SeededSampleCategoryCount);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeaturesStream")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_ReturnsPages()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            Where = "1=1",
            ReturnGeometry = true
        };
        request.OutFields.Add("*");

        var call = _client!.QueryFeaturesStream(request, _headers);
        var pages = new List<Proto.FeaturePage>();

        await foreach (var page in call.ResponseStream.ReadAllAsync())
        {
            pages.Add(page);
        }

        pages.Should().NotBeEmpty();
        pages[^1].IsLastPage.Should().BeTrue();

        // Five rows at a page size of two is three pages of 2, 2 and 1 — not one page of
        // everything, and not an empty stream that "ends" correctly.
        pages.Should().HaveCount(3, "{0} rows at a page size of {1} spans three pages",
            SeededFeatureCount, StreamBatchSize);
        pages.Select(page => page.Features.Count).Should().Equal(2, 2, 1);
        pages.SkipLast(1).Should().OnlyContain(page => !page.IsLastPage);

        // The first page carries the layer descriptor; later pages do not repeat it.
        pages[0].ObjectIdFieldName.Should().Be("objectid");
        pages[0].Fields.Select(field => field.Name).Should().Contain(["objectid", "name", "category"]);
        pages.Skip(1).Should().OnlyContain(page => page.Fields.Count == 0);

        var features = pages.SelectMany(page => page.Features).ToList();
        features.Select(feature => feature.Id).OrderBy(id => id)
            .Should().Equal(
                new[] { 1L, 2L, 3L, 4L, 5L },
                "every seeded row must appear exactly once across the pages");

        // Asserted values, not just ids: names, the category partition, and the ordinates
        // of a row whose position is known.
        var byId = features.ToDictionary(feature => feature.Id);
        byId[1].Attributes["name"].StringValue.Should().Be("Test Feature");
        byId[2].Attributes["name"].StringValue.Should().Be("Another Feature");
        byId[5].Attributes["name"].StringValue.Should().Be("Fifth Feature");
        features.Count(feature => feature.Attributes["category"].StringValue == "test")
            .Should().Be((int)SeededTestCategoryCount);

        byId[1].Geometry.Point.X.Should().BeApproximately(-122.5, 1e-6);
        byId[1].Geometry.Point.Y.Should().BeApproximately(37.5, 1e-6);

        // The seed's one null-geometry row must stream as a row without geometry rather
        // than being dropped or silently given a default point.
        byId[3].Geometry.Should().BeNull("objectid 3 is seeded with a null geometry");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeaturesStream")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_WithResultRecordCount_StopsAtTheRequestedLimit()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            Where = "1=1",
            ReturnGeometry = true,
            ResultRecordCountLong = 3
        };
        request.OutFields.Add("*");

        var call = _client!.QueryFeaturesStream(request, _headers);
        var features = new List<Proto.Feature>();
        var pages = 0;
        await foreach (var page in call.ResponseStream.ReadAllAsync())
        {
            pages++;
            features.AddRange(page.Features);
        }

        // The limit is honored (3 of 5 rows) and still paged at the configured batch size.
        features.Should().HaveCount(3, "result_record_count_long must bound the stream");
        pages.Should().Be(2, "three rows at a page size of {0} spans two pages", StreamBatchSize);
        features.Select(feature => feature.Id).Should().OnlyHaveUniqueItems();
        features.Select(feature => feature.Id).Should().BeSubsetOf(new[] { 1L, 2L, 3L, 4L, 5L });
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_WithAdd_ReturnsResult()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            RollbackOnFailure = true
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = AddedFeatureName } },
            Geometry = new Proto.Geometry
            {
                Point = new Proto.PointGeometry { X = AddedFeatureX, Y = AddedFeatureY }
            }
        });

        var response = await _client!.ApplyEditsAsync(request, _headers);

        response.Should().NotBeNull();
        response.AddResults.Should().ContainSingle();
        response.AddResults[0].Success.Should().BeTrue();
        response.AddResults[0].ObjectId.Should().BeGreaterThan(0);

        var objectId = response.AddResults[0].ObjectId;

        try
        {
            // Read the row back over the protocol that wrote it: an echoed ObjectId proves
            // nothing on its own.
            var readBack = new Proto.QueryFeaturesRequest
            {
                ServiceId = WebAppFixture.TestServiceId,
                LayerId = WebAppFixture.TestLayerId,
                ReturnGeometry = true
            };
            readBack.OutFields.Add("*");
            readBack.ObjectIds.Add(objectId);

            var readBackResponse = await _client.QueryFeaturesAsync(readBack, _headers);
            var stored = readBackResponse.Features.Should().ContainSingle().Subject;
            stored.Id.Should().Be(objectId);
            stored.Attributes["name"].StringValue.Should().Be(AddedFeatureName);
            stored.Geometry.Should().NotBeNull("the add carried a point geometry");
            stored.Geometry.Point.X.Should().BeApproximately(AddedFeatureX, 1e-6);
            stored.Geometry.Point.Y.Should().BeApproximately(AddedFeatureY, 1e-6);

            // ...and in PostGIS, not merely in a response projection.
            (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
                .Should().Be(AddedFeatureName);
            (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId))
                .Should().Be(SeededFeatureCount + 1, "exactly one row was added");
        }
        finally
        {
            await RemoveAddedFeatureAsync(objectId);
        }
    }

    // The host is shared by the class: remove the added row so the seeded counts hold.
    private async Task RemoveAddedFeatureAsync(long objectId)
    {
        var cleanup = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            RollbackOnFailure = true
        };
        cleanup.Deletes.Add(objectId);
        (await _client.ApplyEditsAsync(cleanup, _headers)).DeleteResults.Should().ContainSingle()
            .Which.Success.Should().BeTrue();
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(SeededFeatureCount);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_RejectedEdits_LeaveStoredStateUnchanged()
    {
        const long MissingObjectId = 999_999_999L;
        var before = await ReadEveryFeatureAsync();
        before.Select(feature => feature.Id).Should().Equal(1L, 2L, 3L, 4L, 5L);

        // An update whose target does not exist fails the whole batch closed.
        var update = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            RollbackOnFailure = true
        };
        update.Updates.Add(new Proto.Feature
        {
            Id = MissingObjectId,
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "must-not-persist" } }
        });

        var updateFailure = await Assert.ThrowsAsync<RpcException>(
            () => _client!.ApplyEditsAsync(update, _headers).ResponseAsync);
        updateFailure.StatusCode.Should().Be(StatusCode.NotFound);

        // ...and so does a delete of a row that is not there.
        var delete = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            RollbackOnFailure = true
        };
        delete.Deletes.Add(MissingObjectId);

        var deleteFailure = await Assert.ThrowsAsync<RpcException>(
            () => _client!.ApplyEditsAsync(delete, _headers).ResponseAsync);
        deleteFailure.StatusCode.Should().Be(StatusCode.NotFound);

        // Nothing moved: same ids, same names, same ordinates, read back over gRPC.
        var after = await ReadEveryFeatureAsync();
        after.Select(feature => feature.Id).Should().Equal(before.Select(feature => feature.Id));
        after.Select(feature => feature.Attributes["name"].StringValue)
            .Should().Equal(before.Select(feature => feature.Attributes["name"].StringValue));
        after.Select(DescribePoint).Should().Equal(before.Select(DescribePoint));

        // ...and the store holds no row the refused edits tried to write.
        (await _fixture.CountStoredFeaturesByNameAsync(WebAppFixture.TestLayerId, "must-not-persist"))
            .Should().Be(0);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId))
            .Should().Be(SeededFeatureCount);
    }

    private async Task<long> CountAsync(string? where)
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            ReturnCountOnly = true
        };

        if (where is not null)
        {
            request.Where = where;
        }

        var response = await _client!.QueryFeaturesAsync(request, _headers);
        response.Features.Should().BeEmpty();
        return response.Count;
    }

    private async Task<IReadOnlyList<Proto.Feature>> ReadEveryFeatureAsync()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            Where = "1=1",
            ReturnGeometry = true
        };
        request.OutFields.Add("*");

        var response = await _client!.QueryFeaturesAsync(request, _headers);
        return [.. response.Features.OrderBy(feature => feature.Id)];
    }

    private static string DescribePoint(Proto.Feature feature) => feature.Geometry?.Point is { } point
        ? string.Format(CultureInfo.InvariantCulture, "{0:F6},{1:F6}", point.X, point.Y)
        : "<null>";
}
