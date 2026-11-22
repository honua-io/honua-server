// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed partial class FeatureServerQueryExecutor
{
    // About 30 KB of ids per network write.
    private const int IdsPerPipeFlush = 4_096;

    // Must not exceed IdsPerPipeFlush: nothing reaches the client before this many ids have been read.
    private const int IdsReadBeforeStatus = IdsPerPipeFlush;

    private static async Task StreamObjectIdsWithValidationAsync(IObjectIdStreamingReader reader, ObjectIdStreamRequest request)
    {
        var cancellationToken = request.Response.CancellationToken;
        int? requestedLimit = request.Query.Limit is int limit and >= 0 and < int.MaxValue ? limit : null;
        var streamQuery = requestedLimit is int boundedLimit
            ? request.Query with { Limit = checked(boundedLimit + 1), ReturnPublicObjectIds = true }
            : request.Query with { ReturnPublicObjectIds = true };
        var objectIds = reader
            .StreamObjectIdsAsync(request.LayerId, streamQuery, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using (objectIds.ConfigureAwait(false))
        {
            var leadingCap = requestedLimit is int cap
                ? Math.Min(IdsReadBeforeStatus, checked(cap + 1))
                : IdsReadBeforeStatus;
            var leadingIds = await ReadLeadingObjectIdsAsync(objectIds, leadingCap).ConfigureAwait(false);
            var (remainingIds, response) = ResolveLeadingWindow(
                leadingIds,
                objectIds,
                new ObjectIdLeadingWindowOptions
                {
                    LeadingCap = leadingCap,
                    RequestedLimit = requestedLimit,
                    BaseResponse = request.Response
                });
            try
            {
                await WriteObjectIdsAsync(leadingIds, remainingIds, response).ConfigureAwait(false);
            }
            catch when (request.Response.Context.Response.HasStarted)
            {
                // A 200 is already on the wire; drop the connection so the client cannot mistake a
                // truncated id list for a complete one.
                request.Response.Context.Abort();
                throw;
            }
        }
    }

    private static (IAsyncEnumerator<long>? RemainingIds, ObjectIdResponse Response) ResolveLeadingWindow(
        List<long> leadingIds,
        IAsyncEnumerator<long> objectIds,
        ObjectIdLeadingWindowOptions options)
    {
        if (options.RequestedLimit is not int limitCap)
        {
            var remaining = leadingIds.Count == IdsReadBeforeStatus ? objectIds : null;
            return (remaining, options.BaseResponse with { LeadingCount = leadingIds.Count });
        }

        if (leadingIds.Count > limitCap)
        {
            leadingIds.RemoveRange(limitCap, leadingIds.Count - limitCap);
            return (null, options.BaseResponse with { ExceededTransferLimit = true, LeadingCount = leadingIds.Count });
        }

        if (leadingIds.Count == options.LeadingCap)
        {
            return (objectIds, options.BaseResponse with
            {
                RemainingLimit = limitCap - leadingIds.Count,
                LeadingCount = leadingIds.Count
            });
        }

        return (null, options.BaseResponse with { ExceededTransferLimit = false, LeadingCount = leadingIds.Count });
    }

    // A failing query, including a row-time database error within the first page of ids, surfaces here
    // before anything is written, so it still becomes a normal error response. A failure after that point
    // aborts the started response, as for every streamed query result.
    private static async Task<List<long>> ReadLeadingObjectIdsAsync(IAsyncEnumerator<long> objectIds, int maxCount)
    {
        var leadingIds = new List<long>();
        try
        {
            while (leadingIds.Count < maxCount && await objectIds.MoveNextAsync().ConfigureAwait(false))
            {
                leadingIds.Add(objectIds.Current);
            }

            return leadingIds;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid query.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Invalid query format.", ex);
        }
    }

    private static async Task WriteObjectIdsAsync(
        IReadOnlyList<long> leadingIds,
        IAsyncEnumerator<long>? remainingIds,
        ObjectIdResponse response)
    {
        var context = response.Context;
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status200OK;

        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", response.ObjectIdFieldName);
        writer.WriteStartArray("objectIds");

        var idsWritten = 0L;
        foreach (var objectId in leadingIds)
        {
            writer.WriteNumberValue(objectId);
            await FlushIfDueAsync(writer, ++idsWritten, response).ConfigureAwait(false);
        }

        var exceededTransferLimit = response.ExceededTransferLimit;
        if (remainingIds is not null)
        {
            exceededTransferLimit = await WriteRemainingObjectIdsAsync(
                writer, remainingIds, response with { LeadingCount = idsWritten }).ConfigureAwait(false);
        }

        writer.WriteEndArray();
        if (exceededTransferLimit is { } exceeded)
        {
            writer.WriteBoolean("exceededTransferLimit", exceeded);
        }

        writer.WriteEndObject();

        await writer.FlushAsync(response.CancellationToken).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task<bool?> WriteRemainingObjectIdsAsync(
        Utf8JsonWriter writer,
        IAsyncEnumerator<long> remainingIds,
        ObjectIdResponse response)
    {
        var idsWritten = response.LeadingCount;
        var remainingAllowed = response.RemainingLimit;
        var remainingWritten = 0;
        while (await remainingIds.MoveNextAsync().ConfigureAwait(false))
        {
            if (remainingAllowed is int allowed && remainingWritten >= allowed)
            {
                return true;
            }

            writer.WriteNumberValue(remainingIds.Current);
            remainingWritten++;
            await FlushIfDueAsync(writer, ++idsWritten, response).ConfigureAwait(false);
        }

        return remainingAllowed.HasValue ? false : response.ExceededTransferLimit;
    }

    private static async Task FlushIfDueAsync(Utf8JsonWriter writer, long idsWritten, ObjectIdResponse response)
    {
        if (idsWritten % FlushInterval != 0)
        {
            return;
        }

        await writer.FlushAsync(response.CancellationToken).ConfigureAwait(false);
        if (idsWritten % IdsPerPipeFlush == 0)
        {
            // Utf8JsonWriter only advances the pipe; flushing the pipe is what sends the bytes.
            await response.Context.Response.BodyWriter.FlushAsync(response.CancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct ObjectIdResponse
    {
        public required string ObjectIdFieldName { get; init; }
        public required HttpContext Context { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public bool? ExceededTransferLimit { get; init; }
        public int? RemainingLimit { get; init; }
        public long LeadingCount { get; init; }
    }

    private readonly record struct ObjectIdLeadingWindowOptions
    {
        public int LeadingCap { get; init; }
        public int? RequestedLimit { get; init; }
        public ObjectIdResponse BaseResponse { get; init; }
    }

    private readonly record struct ObjectIdStreamRequest(int LayerId, FeatureQuery Query, ObjectIdResponse Response);
}
