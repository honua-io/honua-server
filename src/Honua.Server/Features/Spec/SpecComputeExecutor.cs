// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Server.Features.Spec;

internal sealed class SpecComputeExecutor : ISpecComputeExecutor
{
    public Task<SpecComputeResult> ExecuteAsync(
        CanonicalSpecNode node,
        string contentHash,
        IReadOnlyDictionary<string, CachedArtifactRef> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        ArgumentNullException.ThrowIfNull(inputs);

        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("nodeId"u8, node.Id);
            writer.WriteString("kind"u8, node.Kind.ToString());
            if (node.Op is not null)
            {
                writer.WriteString("op"u8, node.Op);
            }

            writer.WriteString("contentHash"u8, contentHash);
            writer.WriteStartArray("inputs"u8);
            foreach (var key in inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("nodeId"u8, key);
                writer.WriteString("contentHash"u8, inputs[key].ContentHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = buffer.WrittenMemory.ToArray();
        stopwatch.Stop();

        return Task.FromResult(new SpecComputeResult
        {
            Payload = new SpecArtifactPayload
            {
                ContentHash = contentHash,
                Bytes = bytes,
                ContentType = "application/json"
            },
            ActualCost = new SpecCostActual
            {
                Rows = inputs.Count,
                Bytes = bytes.LongLength,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            }
        });
    }
}
