// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerExceptionMappingTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhenReaderThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateQueryFixture(() => new InvalidOperationException("unexpected query backend failure"));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/rest/services/test/FeatureServer/0/query?where=1%3D1&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("unexpected query backend failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhenReaderThrowsKnownInvalidQueryInvalidOperation_ReturnsBadRequest()
    {
        var fixture = CreateQueryFixture(() => new InvalidOperationException("Invalid query syntax: malformed where clause"));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/rest/services/test/FeatureServer/0/query?where=1%3D1&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhenReaderThrowsNotSupportedException_ReturnsBadRequest()
    {
        // MySQL/MariaDB raises NotSupportedException for client-driven query shapes the provider
        // cannot honour (e.g. distance filter on a polygon layer, projected-SRID distance filter,
        // KNN/nearest-neighbor, cross-SRID transform, extent on unsupported geometry types). Prior
        // to this fix the catch (Exception) fall-through mapped them to 500; the new
        // catch (NotSupportedException) block returns 400 so callers see a client error.
        var fixture = CreateQueryFixture(() => new NotSupportedException(
            "Distance spatial filters require Point or MultiPoint geometry in the MySQL/MariaDB provider."));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/rest/services/test/FeatureServer/0/query?where=1%3D1&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("Distance spatial filters require Point");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhenProviderRouterReportsCapabilityMissing_ReturnsBadRequest()
    {
        // FeatureProviderQueryRouter throws InvalidOperationException("Feature provider 'X' does
        // not support Y operations.") when the resolved provider's capability bit is false (e.g.
        // outStatistics on MySQL/MariaDB which has SupportsStatistics=false). The IsClientSafe
        // check now matches "does not support" so capability mismatches surface as 400 instead
        // of falling through to 500.
        var fixture = CreateQueryFixture(() => new InvalidOperationException(
            "Feature provider 'mysql' does not support statistics operations."));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/rest/services/test/FeatureServer/0/query?where=1%3D1&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("Feature provider 'mysql' does not support");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WhenServiceThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateRelatedRecordsFixture(() => new InvalidOperationException("unexpected related records failure"));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/test/FeatureServer/0/queryRelatedRecords?objectIds=1&relationshipId=1");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("unexpected related records failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WhenServiceThrowsKnownInvalidQueryInvalidOperation_ReturnsBadRequest()
    {
        var fixture = CreateRelatedRecordsFixture(() => new InvalidOperationException("Invalid related query syntax: malformed where clause"));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/test/FeatureServer/0/queryRelatedRecords?objectIds=1&relationshipId=1");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateQueryFixture(Func<Exception> exceptionFactory)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<QueryResult<Feature>>(exceptionFactory()));

        return new WebAppFixture()
            .ReplaceService<IFeatureReader>(reader);
    }

    private static WebAppFixture CreateRelatedRecordsFixture(Func<Exception> exceptionFactory)
    {
        return new WebAppFixture()
            .ReplaceService<IRelatedRecordsService>(new ThrowingRelatedRecordsService(exceptionFactory));
    }

    private sealed class ThrowingRelatedRecordsService(Func<Exception> exceptionFactory) : IRelatedRecordsService
    {
        private readonly Func<Exception> _exceptionFactory = exceptionFactory ?? throw new ArgumentNullException(nameof(exceptionFactory));

        public RelatedQuery BuildRelatedQuery(
            QueryRelatedRecordsParameters queryParams,
            long[] objectIds,
            Relationship relationship,
            SqlFragment? sqlFilter)
        {
            throw _exceptionFactory();
        }

        public Task<QueryResult<Feature>> ExecuteRelatedQueryAsync(
            int layerId,
            RelatedQuery query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(QueryResult<Feature>.Empty());
        }

        public RelatedRecordGroup[] GroupRelatedRecords(
            QueryResult<Feature> result,
            long[] objectIds,
            Relationship relationship,
            string objectIdFieldName,
            bool returnGeometry,
            int? outputSrid,
            bool returnZ,
            bool returnM,
            int? geometryPrecision,
            double? maxAllowableOffset,
            ImmutableArray<string>? outFields)
        {
            return [];
        }
    }
}
