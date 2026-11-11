// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Storage bounds of a job log: 2,000 entries of at most 4 KiB each, 8 MiB of payload per job.
internal static class ExecutionLogEntryBounds
{
    public const int MaxStoredEntries = 2000;
    public const int MaxEntryBytes = 4096;
    public const int MaxEventIdLength = 160;

    public static string SerializeBounded(ExecutionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Bound inputs before serialization so an oversized message/metadata cannot allocate
        // another copy of an arbitrarily large caller payload at this storage boundary.
        var metadata = entry.Metadata?.Take(32).ToDictionary(
            pair => pair.Key, pair => pair.Value[..Math.Min(pair.Value.Length, 128)], StringComparer.Ordinal);
        var truncated = entry.Message.Length > 384 || entry.Phase?.Length > 128 ||
            entry.Metadata is { Count: > 32 } || entry.Metadata?.Any(pair => pair.Key.Length > 128 || pair.Value.Length > 128) == true;
        if (metadata is not null)
        {
            foreach (var keyToRemove in metadata.Keys.Where(key => key.Length > 128).ToArray())
            {
                metadata.Remove(keyToRemove);
            }
        }
        if (truncated)
        {
            metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
            metadata["honua.log.truncated"] = "true";
        }
        var bounded = entry with
        {
            Message = entry.Message[..Math.Min(entry.Message.Length, 384)],
            Phase = entry.Phase is null ? null : entry.Phase[..Math.Min(entry.Phase.Length, 128)],
            Metadata = metadata
        };
        var payload = JsonSerializer.Serialize(bounded, ControlPlaneJsonContext.Default.ExecutionLogEntry);
        if (Encoding.UTF8.GetByteCount(payload) > MaxEntryBytes)
        {
            // Keep canonical event identity/status even when arbitrary metadata exceeds the
            // byte budget. Oversized messages must never erase a terminal event's meaning.
            var canonical = metadata?.Where(pair => pair.Key is "honua.event.v" or "honua.event.kind" or
                    "honua.event.seq" or "honua.event.attempt" or "honua.event.status" or "honua.event.exitCode")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            canonical["honua.log.truncated"] = "true";
            bounded = bounded with { Message = "Execution log payload omitted: 4 KiB entry limit exceeded.", Phase = null, Metadata = canonical };
            payload = JsonSerializer.Serialize(bounded, ControlPlaneJsonContext.Default.ExecutionLogEntry);
        }

        return payload;
    }
}
