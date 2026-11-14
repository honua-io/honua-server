// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.GeoJson;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features;

internal sealed partial class OgcFeaturesQueryHandler
{
    private sealed record CsvStreamQuery(
        MetadataV2Resource Resource,
        int LayerId,
        FeatureQuery Query)
    {
        public required AxisOrder AxisOrder { get; init; }
        public ImmutableHashSet<string>? ProjectedProperties { get; init; }
        public required string CrsUri { get; init; }
    }

    private StreamingCsvItemsResult CreateStreamingCsvResult(
        IStreamingFeatureStore store,
        CsvStreamQuery query,
        CancellationToken cancellationToken)
    {
        return new StreamingCsvItemsResult(
            token => StreamCsvFeaturesAsync(store, query, token),
            new CsvResponseOptions(ResolveCsvFieldNames(query.Resource, query.ProjectedProperties), cancellationToken),
            query.CrsUri);
    }

    private async IAsyncEnumerable<GeoJsonFeature> StreamCsvFeaturesAsync(
        IStreamingFeatureStore store,
        CsvStreamQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = new GeoJsonFeatureBaseBuilder.PreparedSchema(query.Resource, false);
        await foreach (var feature in store.StreamFeaturesAsync(query.LayerId, query.Query, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return ToOgcFeature(feature, query.Resource, query.AxisOrder, _geometryServices,
                query.ProjectedProperties, schema: schema);
        }
    }

    private sealed class StreamingCsvItemsResult(
        Func<CancellationToken, IAsyncEnumerable<GeoJsonFeature>> features,
        CsvResponseOptions options,
        string crsUri) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted, options.CancellationToken);
            httpContext.Response.ContentType = $"{MediaTypes.Csv}; charset=utf-8";
            httpContext.Response.Headers["Content-Crs"] = FormatContentCrs(crsUri);
            try
            {
                await OgcResponseFormatter.StreamCsvResponseAsync(
                    httpContext.Response.BodyWriter.AsStream(leaveOpen: true),
                    features(linkedCts.Token), options with { CancellationToken = linkedCts.Token });
            }
            catch (Exception exception) when (!httpContext.Response.HasStarted &&
                exception is not (OperationCanceledException or OutOfMemoryException))
            {
                var result = exception is ServiceUnavailableException
                    ? StandardErrorHelpers.CreateFromException(httpContext, exception)
                    : StandardErrorHelpers.CreateInternalServerError(httpContext, "An error occurred while retrieving items.");
                await result.ExecuteAsync(httpContext);
            }
        }
    }
}
