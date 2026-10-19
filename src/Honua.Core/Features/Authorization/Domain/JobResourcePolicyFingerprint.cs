// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>Canonical bounded metadata, row-policy and field-mask fingerprint shared by job provenance validators.</summary>
public static class JobResourcePolicyFingerprint
{
    /// <summary>Hashes current policy without persisting SQL, policy values or a reusable authority.</summary>
    /// <param name="resource">Actual current canonical metadata resource.</param>
    /// <param name="policy">Current shared row-filter and field-mask resolution for the original policy identity.</param>
    /// <returns>The stable lowercase SHA256 fingerprint used by resource closures.</returns>
    public static string Compute(MetadataV2Resource resource, ReadSecurityResolution policy)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (policy.MaskedFields.IsDefault)
        {
            throw Denied();
        }
        var filter = policy.RowFilter;
        var masks = policy.MaskedFields;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            var metadata = SerializeBoundedMetadata(resource);
            if (Encoding.UTF8.GetByteCount(metadata) > 256 * 1024 || (filter?.Parameters.Count ?? 0) > 256 || masks.Length > 1024)
            {
                throw Denied();
            }
            writer.Write(CanonicalizeMetadata(metadata));
            if (Encoding.UTF8.GetByteCount(filter?.Sql ?? string.Empty) > 64 * 1024)
            {
                throw Denied();
            }
            writer.Write(filter?.Sql ?? string.Empty);
            writer.Write(filter?.Parameters.Count ?? 0);
            foreach (var parameter in filter?.Parameters ?? [])
            {
                // Only canonical scalar query parameters are fingerprintable. Unknown provider
                // objects fail closed instead of relying on culture-sensitive object.ToString().
                var value = parameter switch
                {
                    null => "null",
                    string text => "string:" + text,
                    bool boolean => "bool:" + (boolean ? "true" : "false"),
                    byte or short or int or long or ushort or uint or ulong or sbyte => "integer:" + Convert.ToString(parameter, CultureInfo.InvariantCulture),
                    decimal number => "decimal:" + number.ToString(CultureInfo.InvariantCulture),
                    double number => "double:" + number.ToString("R", CultureInfo.InvariantCulture),
                    float number => "float:" + number.ToString("R", CultureInfo.InvariantCulture),
                    Guid guid => "guid:" + guid.ToString("D"),
                    DateTimeOffset instant => "datetimeoffset:" + instant.ToString("O", CultureInfo.InvariantCulture),
                    DateTime instant => "datetime:" + instant.ToString("O", CultureInfo.InvariantCulture),
                    _ => throw Denied()
                };
                if (Encoding.UTF8.GetByteCount(value) > 64 * 1024)
                {
                    throw Denied();
                }
                writer.Write(parameter?.GetType().FullName ?? "null");
                writer.Write(value);
                if (buffer.Length > 1024 * 1024)
                {
                    throw Denied();
                }
            }
            writer.Write(masks.Length);
            foreach (var mask in masks.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Encoding.UTF8.GetByteCount(mask) > 1024)
                {
                    throw Denied();
                }
                writer.Write(mask.ToUpperInvariant());
            }
            if (buffer.Length > 1024 * 1024)
            {
                throw Denied();
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
    private static string SerializeBoundedMetadata(MetadataV2Resource resource)
    {
        using var stream = new BoundedMetadataStream();
        JsonSerializer.Serialize(stream, resource, MetadataV2JsonContext.Default.MetadataV2Resource);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private sealed class BoundedMetadataStream : MemoryStream
    {
        private const int Limit = 256 * 1024;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position > Limit - count)
            {
                throw Denied();
            }
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position > Limit - buffer.Length)
            {
                throw Denied();
            }
            base.Write(buffer);
        }
    }

    private static string CanonicalizeMetadata(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteCanonical(item, writer);
            }
            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private static UnauthorizedAccessException Denied()
        => new("The current resource policy cannot be bound to job provenance.");
}
