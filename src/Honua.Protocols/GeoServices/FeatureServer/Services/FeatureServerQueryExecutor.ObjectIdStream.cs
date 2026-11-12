// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed partial class FeatureServerQueryExecutor
{
    // About 30 KB of ids per network write; flushing every few hundred bytes costs more than it saves.
    private const int IdsPerPipeFlush = 4_096;

    // The first id is read before the status line is written, so query validation errors still map to
    // a 400; every later id goes straight from the reader to the response.
    private static async Task StreamObjectIdsWithValidationAsync(
        IObjectIdStreamingReader reader,
        int layerId,
        FeatureQuery query,
        ObjectIdResponse response)
    {
        var cancellationToken = response.CancellationToken;
        var objectIds = reader
            .StreamObjectIdsAsync(layerId, query with { ReturnPublicObjectIds = true }, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using (objectIds.ConfigureAwait(false))
        {
            var hasCurrent = await MoveToFirstObjectIdAsync(objectIds).ConfigureAwait(false);
            await WriteObjectIdsAsync(objectIds, hasCurrent, response).ConfigureAwait(false);
        }
    }

    private static async Task<bool> MoveToFirstObjectIdAsync(IAsyncEnumerator<long> objectIds)
    {
        try
        {
            return await objectIds.MoveNextAsync().ConfigureAwait(false);
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

    private static async Task WriteObjectIdsAsync(IAsyncEnumerator<long> objectIds, bool hasCurrent, ObjectIdResponse response)
    {
        var context = response.Context;
        var cancellationToken = response.CancellationToken;
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
        while (hasCurrent)
        {
            writer.WriteNumberValue(objectIds.Current);
            if (++idsWritten % FlushInterval == 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (idsWritten % IdsPerPipeFlush == 0)
            {
                // Utf8JsonWriter only advances the pipe; flushing the pipe is what sends the bytes.
                await context.Response.BodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            hasCurrent = await objectIds.MoveNextAsync().ConfigureAwait(false);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private readonly record struct ObjectIdResponse(
        string ObjectIdFieldName,
        HttpContext Context,
        CancellationToken CancellationToken);
}
