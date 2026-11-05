// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

internal static class AuditEventJsonSerializer
{
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

    public static byte[] SerializeObject(AuditEvent evt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, evt);
        }

        return buffer.WrittenSpan.ToArray();
    }

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
