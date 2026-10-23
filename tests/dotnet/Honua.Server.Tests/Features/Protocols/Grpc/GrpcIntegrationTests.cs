// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Integration tests for gRPC FeatureService exercised through the full ASP.NET Core pipeline
/// with a real PostgreSQL database via <see cref="WebAppFixture"/>.
/// Uses gRPC-Web transport (HTTP/1.1) since the in-memory test server does not support HTTP/2.
/// </summary>
/// <remarks>
/// Every assertion here is measured against the literal <c>tests/seed/server.yaml</c>
/// denominator for layer <see cref="WebAppFixture.TestLayerId"/>: five rows, objectids
/// 1-5, three of category <c>test</c> and two of category <c>sample</c>, with objectid 3
/// deliberately carrying a null geometry. A response that is merely well-formed — a
/// non-negative count, a non-empty page list, an echoed write — must not pass; each test
/// names the exact rows, ids and values the server has to produce (#4411).
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcIntegrationTests : IAsyncLifetime
{
    /// <summary>Rows the seed publishes on the test layer.</summary>
    private const long SeededFeatureCount = 5;

    /// <summary>Rows the seed publishes with <c>category = 'test'</c>.</summary>
    private const long SeededTestCategoryCount = 3;

    /// <summary>Rows the seed publishes with <c>category = 'sample'</c>.</summary>
    private const long SeededSampleCategoryCount = 2;

    /// <summary>
    /// Streaming page size for this fixture. Chosen so the five seeded rows span three
    /// pages (2 + 2 + 1); with the 1000-row default every read is a single page and the
    /// paging contract is never exercised.
    /// </summary>
    private const int StreamBatchSize = 2;

    private const string AddedFeatureName = "gRPC integration test";
    private const double AddedFeatureX = -157.85;
    private const double AddedFeatureY = 21.30;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Community)
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Grpc:StreamBatchSize"] = StreamBatchSize.ToString(CultureInfo.InvariantCulture)
            })));

    private GrpcChannel? _channel;
    private Proto.FeatureService.FeatureServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Use GrpcWebHandler wrapping the test server's handler.
        // This converts gRPC to gRPC-Web format (HTTP/1.1), and the server's
        // UseGrpcWeb middleware (DefaultEnabled = true) converts back to native gRPC.
        var testServerHandler = _fixture.CreateHandler();
        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, testServerHandler);
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = grpcWebHandler
        });
        _client = new Proto.FeatureService.FeatureServiceClient(_channel);

        // The shared WebAppFixture uses X-Honua-Test-Schema headers for schema-based isolation.
        // gRPC metadata entries become HTTP headers, so the server's schema middleware can read them.
        _headers = new Metadata();
        if (_fixture.CurrentSchema is not null)
        {
            _headers.Add("X-Honua-Test-Schema", _fixture.CurrentSchema);
        }
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();
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
