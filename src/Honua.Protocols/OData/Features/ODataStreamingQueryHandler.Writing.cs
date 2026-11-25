// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;

namespace Honua.Protocols.OData;

internal sealed partial class ODataStreamingQueryHandler
{
    private sealed class ODataFeatureStreamRequest
    {
        public required HttpContext Context { get; init; }
        public required int LayerId { get; init; }
        public int? LayerSrid { get; init; }
        public required AxisOrder AxisOrder { get; init; }
        public required PaginationValues Pagination { get; init; }
        public int? RequestedTop { get; init; }
        public HashSet<string>? SelectedFields { get; init; }
        public required IReadOnlySet<string> HiddenFields { get; init; }
        public string? Filter { get; init; }
        public string? Select { get; init; }
        public string? OrderBy { get; init; }
        public bool? Count { get; init; }
        public string? Expand { get; init; }
        public bool UseSkipToken { get; init; }
        public string? Compute { get; init; }
        public string? Format { get; init; }
        public bool TrackChangesRequested { get; init; }
        public string? DeltaToken { get; init; }
        public required ODataDeltaService.DeltaQueryState DeltaState { get; init; }
        public long? TotalCount { get; init; }
        public bool IncludeContext { get; init; }
        public ImmutableArray<ODataComputeExpression> ComputeExpressions { get; init; }
        public required IGeometryService GeometryService { get; init; }
    }

    private readonly record struct StreamedPageSummary
    {
        public int StreamedCount { get; init; }
        public bool HasMoreResults { get; init; }
    }

    private static async Task StreamODataFeaturesAsync(
        IAsyncEnumerable<Feature> features,
        ODataFeatureStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.DeltaState);

        using var writer = new Utf8JsonWriter(request.Context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        try
        {
            WriteStreamHeader(writer, request);
            var page = await WriteStreamedFeaturePageAsync(writer, (features, request), cancellationToken);
            writer.WriteEndArray();
            WriteStreamedContinuationLink(writer, request, page);
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }
        catch
        {
            writer.Reset();
            throw;
        }
    }

    private static void WriteStreamHeader(Utf8JsonWriter writer, ODataFeatureStreamRequest request)
    {
        writer.WriteStartObject();
        var baseUrl = ODataUtilityService.GetBaseUrl(request.Context.Request);
        if (request.IncludeContext)
        {
            writer.WriteString(
                "@odata.context",
                ODataUtilityService.BuildContextUrl(baseUrl, "Features", select: request.Select, expand: request.Expand));
        }

        if (request.Count == true && request.TotalCount.HasValue)
        {
            writer.WriteNumber("@odata.count", request.TotalCount.Value);
        }

        writer.WriteStartArray("value");
    }

    private static async Task<StreamedPageSummary> WriteStreamedFeaturePageAsync(
        Utf8JsonWriter writer,
        (IAsyncEnumerable<Feature> Features, ODataFeatureStreamRequest Request) input,
        CancellationToken cancellationToken)
    {
        var (features, request) = input;
        var writeContext = new ODataFeatureWriteContext
        {
            LayerId = request.LayerId,
            LayerSrid = request.LayerSrid,
            AxisOrder = request.AxisOrder,
            Select = request.SelectedFields,
            HiddenFields = request.HiddenFields,
            ComputeExpressions = request.ComputeExpressions,
            GeometryService = request.GeometryService,
            CancellationToken = cancellationToken
        };
        var streamedCount = 0;
        var hasMoreResults = false;
        var featuresSinceFlush = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            if (streamedCount >= request.Pagination.Limit)
            {
                hasMoreResults = true;
                break;
            }

            WriteODataFeature(writer, feature, writeContext);
            streamedCount++;
            if (++featuresSinceFlush >= FlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                await request.Context.Response.BodyWriter.FlushAsync(cancellationToken);
                featuresSinceFlush = 0;
            }
        }

        return new StreamedPageSummary { StreamedCount = streamedCount, HasMoreResults = hasMoreResults };
    }

    private static void WriteStreamedContinuationLink(
        Utf8JsonWriter writer,
        ODataFeatureStreamRequest request,
        StreamedPageSummary page)
    {
        var shouldPaginate = request.TotalCount.HasValue
            ? ODataUtilityService.ShouldPaginate(page.StreamedCount, request.Pagination.Offset, request.TotalCount.Value, request.Pagination.Limit)
            : page.HasMoreResults;
        int? continuationTop = request.Pagination.Limit;
        if (shouldPaginate && !request.TrackChangesRequested && string.IsNullOrWhiteSpace(request.DeltaToken))
        {
            shouldPaginate = ODataUtilityService.TryGetContinuationTop(request.RequestedTop, page.StreamedCount, out continuationTop);
        }

        if (shouldPaginate)
        {
            writer.WriteString("@odata.nextLink", BuildNextLink(request, continuationTop));
            return;
        }

        if (request.TrackChangesRequested)
        {
            var finalDeltaState = !string.IsNullOrWhiteSpace(request.DeltaToken)
                ? request.DeltaState with
                {
                    Timestamp = request.DeltaState.UpperBoundTimestamp ?? DateTimeOffset.UtcNow,
                    UpperBoundTimestamp = null,
                    LayerId = request.LayerId,
                    Count = request.Count
                }
                : request.DeltaState with { LayerId = request.LayerId, Count = request.Count };
            writer.WriteString("@odata.deltaLink", ODataUtilityService.GenerateDeltaLink(request.Context.Request, finalDeltaState));
        }
    }

    private static string BuildNextLink(ODataFeatureStreamRequest request, int? continuationTop)
    {
        var nextSkip = ODataUtilityService.CalculateNextSkip(request.Pagination.Offset, request.Pagination.Limit);
        return !string.IsNullOrWhiteSpace(request.DeltaToken)
            ? ODataUtilityService.GenerateDeltaNextLink(
                request.Context.Request,
                request.DeltaState,
                nextSkip,
                request.Pagination.Limit,
                request.UseSkipToken,
                request.DeltaState.Filter,
                request.DeltaState.OrderBy)
            : ODataUtilityService.GenerateNextLink(
                request.Context.Request,
                nextSkip,
                continuationTop,
                request.Filter,
                request.Select,
                request.OrderBy,
                request.Count,
                request.Expand,
                request.UseSkipToken,
                request.Compute,
                request.Format,
                request.TrackChangesRequested,
                request.TrackChangesRequested ? request.DeltaState : null);
    }
}
