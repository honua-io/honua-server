// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Reflection-free serializer that renders an <see cref="AuditEvent"/> as a JSON
/// object using <see cref="Utf8JsonWriter"/>. Kept hand-written so the export
/// sinks stay AOT/trim-safe and do not depend on a runtime serializer context.
/// </summary>
internal static class AuditEventJsonSerializer
{
    /// <summary>
    /// Writes a single <see cref="AuditEvent"/> as a JSON object to the writer.
    /// <c>Details</c> is emitted as a raw JSON value when it parses as JSON,
    /// otherwise as a JSON string, so already-structured detail is not double-encoded.
    /// </summary>
    /// <param name="writer">The target writer.</param>
    /// <param name="evt">The event to serialize.</param>
    public static void Write(Utf8JsonWriter writer, AuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(evt);

        writer.WriteStartObject();
        writer.WriteString("timestamp", evt.Timestamp.ToString("o", CultureInfo.InvariantCulture));
        writer.WriteString("eventType", evt.EventType.ToString());
        writer.WriteString("actor", evt.Actor);
        writer.WriteString("actorType", evt.ActorType.ToString());
        writer.WriteString("resourceType", evt.ResourceType);
        if (evt.ResourceId is null)
        {
            writer.WriteNull("resourceId");
        }
        else
        {
            writer.WriteString("resourceId", evt.ResourceId);
        }

        writer.WriteString("action", evt.Action);
        writer.WriteString("outcome", evt.Outcome.ToString());
        writer.WriteString("correlationId", evt.CorrelationId);
        if (evt.RemoteIp is null)
        {
            writer.WriteNull("remoteIp");
        }
        else
        {
            writer.WriteString("remoteIp", evt.RemoteIp);
        }

        if (evt.UserAgent is null)
        {
            writer.WriteNull("userAgent");
        }
        else
        {
            writer.WriteString("userAgent", evt.UserAgent);
        }

        WriteDetails(writer, evt.Details);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Serializes a single event to a UTF-8 JSON object byte array.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <returns>UTF-8 encoded JSON.</returns>
    public static byte[] SerializeObject(AuditEvent evt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, evt);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Serializes a batch of events to a UTF-8 JSON array.
    /// </summary>
    /// <param name="events">The events.</param>
    /// <returns>UTF-8 encoded JSON array.</returns>
    public static byte[] SerializeArray(IReadOnlyList<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var evt in events)
            {
                Write(writer, evt);
            }

            writer.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Serializes a batch of events as newline-delimited JSON (one object per line).
    /// </summary>
    /// <param name="events">The events.</param>
    /// <returns>UTF-8 encoded JSON Lines.</returns>
    public static byte[] SerializeNdjson(IReadOnlyList<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var builder = new StringBuilder(256 * Math.Max(1, events.Count));
        foreach (var evt in events)
        {
            builder.Append(Encoding.UTF8.GetString(SerializeObject(evt)));
            builder.Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteDetails(Utf8JsonWriter writer, string details)
    {
        if (string.IsNullOrEmpty(details))
        {
            writer.WriteString("details", string.Empty);
            return;
        }

        try
        {
            using var parsed = JsonDocument.Parse(details);
            writer.WritePropertyName("details");
            parsed.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            // Not valid JSON — store the opaque string as-is.
            writer.WriteString("details", details);
        }
    }
}
