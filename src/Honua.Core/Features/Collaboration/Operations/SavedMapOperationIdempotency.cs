// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// Compares a saved-map append with the operation already stored for the same actor and id or key.
/// </summary>
public static class SavedMapOperationIdempotency
{
    /// <summary>
    /// Client-safe explanation when an id or key is reused for a different request.
    /// </summary>
    public const string MismatchMessage =
        "The operation id or idempotency key was already accepted for this actor with a different request.";

    /// <summary>
    /// True when <paramref name="stored"/> and <paramref name="request"/> are the same actor, kind,
    /// and canonical payload. Object key order is ignored; array order and number lexemes are not.
    /// </summary>
    public static bool Matches(SavedMapOperationEnvelope stored, SavedMapOperationAppendRequest request)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(request);
        return stored.ActorId == request.ActorId &&
            stored.Kind == request.Kind &&
            string.Equals(CanonicalPayload(stored.Payload), CanonicalPayload(request.Payload), StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonical JSON for an idempotency payload comparison.
    /// </summary>
    public static string CanonicalPayload(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "null";
        }

        var buffer = new StringBuilder();
        WriteCanonical(buffer, payload);
        return buffer.ToString();
    }

    private static void WriteCanonical(StringBuilder buffer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                buffer.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        buffer.Append(',');
                    }

                    firstProperty = false;
                    buffer.Append(JsonSerializer.Serialize(property.Name));
                    buffer.Append(':');
                    WriteCanonical(buffer, property.Value);
                }

                buffer.Append('}');
                break;
            case JsonValueKind.Array:
                buffer.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        buffer.Append(',');
                    }

                    firstItem = false;
                    WriteCanonical(buffer, item);
                }

                buffer.Append(']');
                break;
            default:
                buffer.Append(element.GetRawText());
                break;
        }
    }
}
