// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

// A read-only POST (query, search) shares the GET snapshot schedule instead of probing the catalog
// revision on every request (#7393); writes and admin requests still read the current revision.
public sealed class HttpMetadataGraphFreshnessRequirementTests
{
    private static readonly string[] ReadOnlyPostRoutes =
    [
        "/rest/services/{serviceId}/FeatureServer/{layerId:int}/query",
        "/rest/services/{serviceId}/FeatureServer/query",
        "/rest/services/{serviceId}/MapServer/{layerId:int}/query",
        "/rest/services/{serviceId}/MapServer/query",
        "/stac/search",
        "/sharing/rest/search",
    ];

    [UnitTest]
    public void NoRequest_RequiresCurrentRevision()
        => Requirement(context: null).RequireCurrentRevision.Should().BeTrue();

    [UnitTest]
    public void Get_UsesRecentSnapshot()
        => Requirement(Request(HttpMethods.Get, "/rest/services/parcels/FeatureServer/0/query"))
            .RequireCurrentRevision.Should().BeFalse();

    [UnitTest]
    public void UnmarkedPost_RequiresCurrentRevision()
        => Requirement(Request(HttpMethods.Post, "/rest/services/parcels/FeatureServer/0/applyEdits"))
            .RequireCurrentRevision.Should().BeTrue();

    [UnitTest]
    public void ReadOnlyPost_UsesRecentSnapshot()
        => Requirement(Request(HttpMethods.Post, "/rest/services/parcels/FeatureServer/0/query", readOnly: true))
            .RequireCurrentRevision.Should().BeFalse();

    [UnitTest]
    public void ReadOnlyMarker_DoesNotRelaxOtherWriteMethods()
        => Requirement(Request(HttpMethods.Put, "/rest/services/parcels/FeatureServer/0/query", readOnly: true))
            .RequireCurrentRevision.Should().BeTrue();

    [UnitTest]
    public void ReadOnlyPostOnAdminPath_RequiresCurrentRevision()
        => Requirement(Request(HttpMethods.Post, "/api/admin/services/query", readOnly: true))
            .RequireCurrentRevision.Should().BeTrue();

    // 100 POST queries within one revalidation interval on one node: the catalog revision probe (a
    // primary-key read plus a pool checkout) ran once per request before #7393 and runs once now.
    [UnitTest]
    public async Task HundredPostQueries_WithinOneInterval_ProbeTheCatalogOnce()
    {
        (await ProbesFor(readOnly: false)).Should().Be(100, "an unmarked POST still reads the current revision");
        (await ProbesFor(readOnly: true)).Should().Be(1);
    }

    [UnitTest]
    public void RouteTable_MarksQueryAndSearchPostsOnly()
    {
        using var host = new TestWebApplicationFactory();
        using var _ = host.CreateClient();
        var endpoints = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var marked = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ReadOnlyRequestMetadata>() is not null)
            .ToArray();
        var markedPatterns = marked.Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/')).ToArray();

        markedPatterns.Should().Contain(ReadOnlyPostRoutes);
        markedPatterns.Count(pattern => pattern.EndsWith("/ImageServer/query", StringComparison.Ordinal))
            .Should().Be(2, "both image catalog POST queries (by id and by service name) are marked");
        marked.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.SequenceEqual(new[] { HttpMethods.Post }),
            "the marker only relaxes POST");
        markedPatterns.Should().NotContain(pattern => pattern.Contains("admin", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> ProbesFor(bool readOnly)
    {
        var counting = new CountingRevisionProbe();
        var schedule = new MetadataV2GraphRevisionProbeSchedule(TimeProvider.System);
        for (var i = 0; i < 100; i++)
        {
            var requirement = Requirement(Request(HttpMethods.Post, "/rest/services/parcels/FeatureServer/0/query", readOnly));
            var probe = new IntervalMetadataV2GraphRevisionProbe(counting, schedule,
                new MetadataV2GraphRevalidation(TimeSpan.FromSeconds(2), requirement.RequireCurrentRevision));
            await probe.GetCurrentEtagAsync();
        }

        return counting.Calls;
    }

    private sealed class CountingRevisionProbe : IMetadataV2GraphRevisionProbe
    {
        public int Calls { get; private set; }

        public ValueTask<string?> GetCurrentEtagAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<string?>("etag-1");
        }
    }

    private static HttpMetadataGraphFreshnessRequirement Requirement(HttpContext? context)
        => new(new HttpContextAccessor { HttpContext = context });

    private static DefaultHttpContext Request(string method, string path, bool readOnly = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        var metadata = readOnly ? new EndpointMetadataCollection(ReadOnlyRequestMetadata.Instance) : EndpointMetadataCollection.Empty;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return context;
    }
}
