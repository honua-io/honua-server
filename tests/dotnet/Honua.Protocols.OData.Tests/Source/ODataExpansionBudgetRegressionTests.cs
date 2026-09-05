// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.OData;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataExpansionBudgetRegressionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private readonly ConcurrentQueue<RelatedQuery> _relatedQueries = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        _fixture.ConfigureServices(services =>
        {
            services.Configure<ODataOptions>(options => options.MaxPageSize = 1);
            var registration = services.Last(descriptor => descriptor.ServiceType == typeof(IRelationshipStore));
            services.Remove(registration);
            services.Add(new ServiceDescriptor(typeof(IRelationshipStore), provider =>
            {
                var inner = (IRelationshipStore)(registration.ImplementationInstance
                    ?? registration.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.GetServiceOrCreateInstance(provider, registration.ImplementationType!));
                return new RecordingRelationshipStore(inner, _relatedQueries);
            }, registration.Lifetime));
        });
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/odata/Layers(0)/Features", "", "InvalidQuery")]
    [InlineData("/odata/Layers(0)/Features", "&$search=San", "InvalidQuery")]
    [InlineData("/odata/Features(0)/$search", "&$search=San", "InvalidQueryOption")]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Expand_OneRowPage_RejectsOverBudgetChildren(string path, string search, string errorCode)
    {
        // The seed has two landmarks for city 1: one more than the configured budget.
        using var response = await _fixture.Client.GetAsync(
            path + "?$filter=ObjectId eq 1&$top=1&$expand=Landmarks" + search);

        _relatedQueries.Should().NotBeEmpty();
        _relatedQueries.Should().OnlyContain(query => query.Limit.HasValue && query.Limit <= 2,
            "the provider must receive the budget plus one overflow probe before materialization");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(errorCode);
    }

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Expand_ExactlyAtBudget_ReturnsCompleteChildren()
    {
        using var response = await _fixture.Client.GetAsync(
            "/odata/Layers(0)/Features?$filter=ObjectId eq 3&$top=1&$expand=Landmarks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("value")[0].GetProperty("Landmarks").GetArrayLength().Should().Be(1);
    }

    private sealed class RecordingRelationshipStore(
        IRelationshipStore inner, ConcurrentQueue<RelatedQuery> queries) : IRelationshipStore
    {
        public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
        {
            queries.Enqueue(query);
            return inner.QueryRelatedAsync(layerId, query, cancellationToken);
        }
    }
}
