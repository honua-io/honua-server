// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wmts;

internal static partial class WmtsRequestHandlers
{
    private readonly record struct WmtsLayerReaderTarget(
        MetadataV2Service Service,
        WmtsLayer Layer,
        IFeatureReader Fallback);

    private sealed class WmtsFeatureInfoQueryInput
    {
        public required SpatialFilter SpatialFilter { get; init; }
        public required int ServiceSrid { get; init; }
        public required int Limit { get; init; }
        public TemporalFilter? TemporalFilter { get; init; }
    }

    private sealed class WmtsFeatureInfoFormatOptions
    {
        public required string LayerName { get; init; }
        public required IReadOnlySet<string> HiddenFields { get; init; }
        public required string InfoFormat { get; init; }
        public required int Limit { get; init; }
    }

    private static async Task<IFeatureReader> ResolveLayerReaderAsync(
        HttpContext context,
        WmtsLayerReaderTarget target,
        CancellationToken cancellationToken)
    {
        var router = context.RequestServices.GetService<FeatureProviderQueryRouter>();
        if (router is null || target.Layer.Snapshot is null)
        {
            return target.Fallback;
        }

        var read = await StorageBoundFeatureReader.ResolveAsync(
                target.Fallback,
                router,
                target.Layer.Snapshot,
                target.Service,
                target.Layer.Resource,
                target.Layer.Publication,
                target.Layer.StorageLayerId,
                FeatureProviderReadOperation.Query,
                cancellationToken)
            .ConfigureAwait(false);
        return read.UseInjectedReader ? target.Fallback : read.Reader;
    }

    private static (FeatureQuery Query, IResult? Error) BuildWmtsFeatureInfoQuery(
        HttpContext context,
        WmtsLayer layer,
        WmtsFeatureInfoQueryInput input)
    {
        var queryProcessor = context.RequestServices.GetService<IQueryProcessor>();
        if (queryProcessor is null)
        {
            return (new FeatureQuery
            {
                SpatialFilter = input.SpatialFilter,
                SpatialReferenceSrid = input.ServiceSrid,
                OutputSrid = input.ServiceSrid,
                Limit = input.Limit,
                TemporalFilter = input.TemporalFilter
            }, null);
        }

        var unifiedQuery = new UnifiedQuery
        {
            SpatialFilter = input.SpatialFilter,
            OutputCrs = QueryCrs.Create(input.ServiceSrid),
            Limit = input.Limit
        };
        var validation = queryProcessor.ValidateQuery(unifiedQuery, layer.Resource);
        if (!validation.IsValid)
        {
            return (default, CreateWmtsExceptionReport(
                context, "InvalidParameterValue", "GetFeatureInfo", validation.ErrorMessage ?? "Invalid feature query."));
        }

        var featureQuery = queryProcessor.ToFeatureQuery(unifiedQuery, layer.Resource) with
        {
            SpatialReferenceSrid = input.ServiceSrid,
            OutputSrid = input.ServiceSrid,
            TemporalFilter = input.TemporalFilter
        };
        return (featureQuery, null);
    }

    private static IResult FormatWmtsFeatureInfoResponse(
        ImmutableArray<Feature> items,
        WmtsFeatureInfoFormatOptions options)
    {
        var remaining = options.Limit;
        var isJson = string.Equals(options.InfoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase);
        var plainText = new StringBuilder();
        var jsonFeatures = new List<OgcClassicFeatureInfoFeature>();

        foreach (var item in items)
        {
            if (remaining-- <= 0)
            {
                break;
            }

            var attributes = BuildVisibleFeatureInfoAttributes(item, options.HiddenFields);
            if (isJson)
            {
                jsonFeatures.Add(new OgcClassicFeatureInfoFeature { Layer = options.LayerName, Attributes = attributes });
                continue;
            }

            AppendPlainTextFeatureInfo(plainText, options.LayerName, attributes);
        }

        if (isJson)
        {
            return Results.Json(
                new OgcClassicFeatureInfoResponse { Features = [.. jsonFeatures] },
                OgcClassicJsonContext.Default.OgcClassicFeatureInfoResponse,
                contentType: JsonMimeType);
        }

        var body = plainText.Length > 0 ? plainText.ToString().TrimEnd() : "No features found.";
        return Results.Content(body, PlainTextMimeType);
    }

    private static void AppendPlainTextFeatureInfo(
        StringBuilder plainText,
        string layerName,
        IReadOnlyDictionary<string, object?> attributes)
    {
        plainText.Append("Layer=").Append(layerName).AppendLine();
        foreach (var attribute in attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            plainText.Append(attribute.Key)
                .Append('=')
                .Append(FormatFeatureInfoValue(attribute.Value))
                .AppendLine();
        }

        plainText.AppendLine();
    }
}
