// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.Core.Features.Authorization.Domain;

// The one canonical digest every seal over a stored submitter snapshot signs: the binding (the record
// and the work it authorizes) and each snapshot field, every part length-prefixed so no two different
// inputs encode the same. It names its fields explicitly, so adding a property does not change the
// digest of a stored snapshot.
public static class JobSecurityContextDigest
{
    public static string Binding(params string?[] parts) => Encode(parts);

    // A JSON binding part in canonical form: object members in ordinal key order. A record read back
    // from a jsonb column has its dictionary keys reordered (jsonb sorts them), so the JSON serialized
    // from it differs from the JSON serialized before the write; canonical form is the same for both.
    public static string? CanonicalJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Compute(string binding, JobSecurityContext snapshot)
    {
        var fields = new List<string?>
        {
            binding,
            snapshot.PrincipalId,
            snapshot.TenantId,
            snapshot.RoleClaimType,
            snapshot.OwnerActorId,
            snapshot.WorkspaceOwnerId,
        };
        foreach (var claim in snapshot.Claims)
        {
            fields.Add(claim.Type);
            fields.Add(claim.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(fields))));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteCanonicalObject(writer, element);
                break;
            case JsonValueKind.Array:
                WriteCanonicalArray(writer, element);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteCanonicalObject(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            WriteCanonical(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalArray(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartArray();
        foreach (var item in element.EnumerateArray())
        {
            WriteCanonical(writer, item);
        }

        writer.WriteEndArray();
    }

    private static string Encode(IEnumerable<string?> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(part is null ? "-" : $"{part.Length}:{part}").Append('\n');
        }

        return builder.ToString();
    }
}
