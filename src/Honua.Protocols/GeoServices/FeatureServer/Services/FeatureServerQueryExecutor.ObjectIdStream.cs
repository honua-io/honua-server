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
        var objectIds = reader
            .StreamObjectIdsAsync(request.LayerId, request.Query with { ReturnPublicObjectIds = true }, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using (objectIds.ConfigureAwait(false))
        {
            var leadingIds = await ReadLeadingObjectIdsAsync(objectIds).ConfigureAwait(false);
            var remainingIds = leadingIds.Count == IdsReadBeforeStatus ? objectIds : null;
            try
            {
                await WriteObjectIdsAsync(leadingIds, remainingIds, request.Response).ConfigureAwait(false);
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

    // A failing query, including a row-time database error within the first page of ids, surfaces here
    // before anything is written, so it still becomes a normal error response. A failure after that point
    // aborts the started response, as for every streamed query result.
    private static async Task<List<long>> ReadLeadingObjectIdsAsync(IAsyncEnumerator<long> objectIds)
    {
        var leadingIds = new List<long>();
        try
        {
            while (leadingIds.Count < IdsReadBeforeStatus && await objectIds.MoveNextAsync().ConfigureAwait(false))
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

        while (remainingIds is not null && await remainingIds.MoveNextAsync().ConfigureAwait(false))
        {
            writer.WriteNumberValue(remainingIds.Current);
            await FlushIfDueAsync(writer, ++idsWritten, response).ConfigureAwait(false);
        }

        writer.WriteEndArray();
        if (response.ExceededTransferLimit is { } exceededTransferLimit)
        {
            writer.WriteBoolean("exceededTransferLimit", exceededTransferLimit);
        }

        writer.WriteEndObject();

        await writer.FlushAsync(response.CancellationToken).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
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

    private readonly record struct ObjectIdResponse(
        string ObjectIdFieldName,
        HttpContext Context,
        CancellationToken CancellationToken,
        bool? ExceededTransferLimit = null);

    private readonly record struct ObjectIdStreamRequest(int LayerId, FeatureQuery Query, ObjectIdResponse Response);
}
