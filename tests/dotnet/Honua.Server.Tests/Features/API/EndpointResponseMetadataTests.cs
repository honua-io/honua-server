// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.API;

/// <summary>
/// Verifies response metadata advertised on endpoints matches runtime-supported media types.
/// </summary>
public sealed class EndpointResponseMetadataTests : IDisposable
{
    private static readonly Regex RouteConstraintRegex =
        new(@"\{([^{}:]+):[^{}]+\}", RegexOptions.Compiled);

    private readonly TestWebApplicationFactory _factory = new();

    [Fact]
    [Trait("Category", "Architecture")]
    public void ODataBatchEndpoint_AdvertisesJsonAndMultipartResponses()
    {
        using var _ = _factory.CreateClient();

        var contentTypes = GetSuccessContentTypes("POST", "/odata/$batch");

        contentTypes.Should().Contain("application/json");
        contentTypes.Should().Contain("multipart/mixed");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FeatureServerQueryEndpoints_AdvertiseAllSupportedFormats()
    {
        using var _ = _factory.CreateClient();

        var layerQueryContentTypes = new[]
        {
            "application/json",
            "application/geo+json",
            "application/x-protobuf",
            "application/vnd.flatgeobuf",
            "application/geobuf",
            "application/vnd.apache.parquet",
            "application/vnd.apache.arrow.stream"
        };

        foreach (var (method, path) in new[]
                 {
                     ("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/query")
                 })
        {
            var contentTypes = GetSuccessContentTypes(method, path);
            contentTypes.Should().Contain(layerQueryContentTypes, $"{method} {path} must advertise every runtime-supported response format");
        }

        var serviceQueryContentTypes = GetSuccessContentTypes("GET", "/rest/services/{serviceId}/FeatureServer/query");
        serviceQueryContentTypes.Should().Contain("application/json",
            "GET /rest/services/{serviceId}/FeatureServer/query returns a multi-layer service response that is currently JSON-only");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FeatureServerEditAndReplicationEndpoints_AdvertiseJsonResponsesAndErrorStatuses()
    {
        using var _ = _factory.CreateClient();

        foreach (var (method, path, expectedType) in new[]
                 {
                     ("POST", "/rest/services/{serviceId}/FeatureServer/applyEdits", typeof(Honua.Server.Features.FeatureServer.Models.ServiceApplyEditsResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures", typeof(Honua.Server.Features.FeatureServer.Models.ApplyEditsResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures", typeof(Honua.Server.Features.FeatureServer.Models.ApplyEditsResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures", typeof(Honua.Server.Features.FeatureServer.Models.ApplyEditsResponse)),
                     ("GET", "/rest/services/{serviceId}/FeatureServer/replicas", typeof(Honua.Server.Features.FeatureServer.Models.ReplicaSummary[])),
                     ("GET", "/rest/services/{serviceId}/FeatureServer/replicas/{replicaId}", typeof(Honua.Server.Features.FeatureServer.Models.ReplicaInfoResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/createReplica", typeof(Honua.Server.Features.FeatureServer.Models.CreateReplicaResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/extractChanges", typeof(Honua.Server.Features.FeatureServer.Models.ExtractChangesResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/synchronizeReplica", typeof(Honua.Server.Features.FeatureServer.Models.SynchronizeReplicaResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/unRegisterReplica", typeof(Honua.Server.Features.FeatureServer.Models.SuccessResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/append", typeof(Honua.Server.Features.FeatureServer.Models.AppendResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/append", typeof(Honua.Server.Features.FeatureServer.Models.AppendResponse)),
                     ("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/calculate", typeof(Honua.Server.Features.FeatureServer.Models.CalculateResponse)),
                     ("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/calculate", typeof(Honua.Server.Features.FeatureServer.Models.CalculateResponse))
                 })
        {
            var successMetadata = GetResponseMetadata(method, path, StatusCodes.Status200OK);
            successMetadata.Should().ContainSingle(metadata => metadata.Type == expectedType);
            successMetadata.SelectMany(metadata => metadata.ContentTypes)
                .Should().Contain("application/json", $"{method} {path} must advertise JSON responses");

            GetResponseMetadata(method, path, StatusCodes.Status400BadRequest).Should().NotBeEmpty();
            GetResponseMetadata(method, path, StatusCodes.Status404NotFound).Should().NotBeEmpty();
        }

        GetResponseMetadata("POST", "/rest/services/{serviceId}/FeatureServer/createReplica", StatusCodes.Status503ServiceUnavailable)
            .Should().NotBeEmpty();
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HashSet<string> GetSuccessContentTypes(string method, string path)
        => GetResponseMetadata(method, path, StatusCodes.Status200OK)
            .SelectMany(metadata => metadata.ContentTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IProducesResponseTypeMetadata[] GetResponseMetadata(string method, string path, int statusCode)
    {
        var endpoint = _factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
            {
                var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return httpMethods != null
                    && httpMethods.Contains(method, StringComparer.OrdinalIgnoreCase)
                    && string.Equals(NormalizePath(endpoint.RoutePattern.RawText ?? string.Empty), path, StringComparison.OrdinalIgnoreCase);
            });

        return endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode == statusCode)
            .ToArray();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return RouteConstraintRegex.Replace(path, "{$1}");
    }
}
