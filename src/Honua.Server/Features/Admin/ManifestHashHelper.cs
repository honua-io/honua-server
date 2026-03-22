// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Shared helpers for computing canonical JSON hashes used in manifest drift detection.
/// </summary>
internal static class ManifestHashHelper
{
    /// <summary>
    /// Default namespace value used when a resource omits its namespace.
    /// </summary>
    public const string DefaultNamespace = "default";

    /// <summary>
    /// Computes a SHA-256 hash of the canonicalized JSON spec.
    /// </summary>
    public static string ComputeSpecHash(JsonElement spec)
    {
        var raw = CanonicalizeJson(spec);
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Produces a canonical JSON string by sorting object properties alphabetically.
    /// </summary>
    public static string CanonicalizeJson(JsonElement element)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonicalJson(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Parses declared resources from a stored manifest JSON array.
    /// </summary>
    public static List<(MetadataResourceIdentifier Id, JsonElement Spec)> ParseDeclaredResources(JsonElement manifestJson)
    {
        var results = new List<(MetadataResourceIdentifier, JsonElement)>();

        if (manifestJson.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in manifestJson.EnumerateArray())
        {
            var kind = element.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
            var metadata = element.TryGetProperty("metadata", out var metaProp) ? metaProp : default;
            var name = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() : null;
            var ns = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("namespace", out var nsProp)
                ? nsProp.GetString() : DefaultNamespace;
            var spec = element.TryGetProperty("spec", out var specProp) ? specProp : default;

            if (!string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(name) && spec.ValueKind != JsonValueKind.Undefined)
            {
                results.Add((new MetadataResourceIdentifier(kind, ns ?? DefaultNamespace, name), spec));
            }
        }

        return results;
    }

    /// <summary>
    /// Computes drift records by comparing a baseline manifest snapshot against actual resources.
    /// </summary>
    public static List<ManifestDriftRecord> ComputeDrift(
        JsonElement baselineManifestJson,
        IReadOnlyList<MetadataResource> actualResources,
        bool verbose = false)
    {
        var driftRecords = new List<ManifestDriftRecord>();
        var declaredResources = ParseDeclaredResources(baselineManifestJson);
        var declaredLookup = new Dictionary<string, (MetadataResourceIdentifier Id, JsonElement Spec)>(StringComparer.Ordinal);

        foreach (var (id, spec) in declaredResources)
        {
            declaredLookup[$"{id.Kind}|{id.Namespace}|{id.Name}"] = (id, spec);
        }

        var actualLookup = new HashSet<string>(StringComparer.Ordinal);

        foreach (var actual in actualResources)
        {
            var key = $"{actual.Kind}|{actual.Metadata?.Namespace ?? DefaultNamespace}|{actual.Metadata?.Name}";
            actualLookup.Add(key);

            if (declaredLookup.TryGetValue(key, out var declared))
            {
                var declaredHash = ComputeSpecHash(declared.Spec);
                var actualHash = ComputeSpecHash(actual.Spec);

                if (!string.Equals(declaredHash, actualHash, StringComparison.Ordinal))
                {
                    driftRecords.Add(new ManifestDriftRecord
                    {
                        Identifier = declared.Id,
                        DriftType = DriftTypes.SpecDrift,
                        DeclaredHash = declaredHash,
                        ActualHash = actualHash,
                        DeclaredSpec = verbose ? declared.Spec : null,
                        ActualSpec = verbose ? actual.Spec : null
                    });
                }
            }
            else
            {
                driftRecords.Add(new ManifestDriftRecord
                {
                    Identifier = new MetadataResourceIdentifier(
                        actual.Kind ?? string.Empty,
                        actual.Metadata?.Namespace ?? DefaultNamespace,
                        actual.Metadata?.Name ?? string.Empty),
                    DriftType = DriftTypes.Extra,
                    ActualHash = ComputeSpecHash(actual.Spec)
                });
            }
        }

        foreach (var (key, declared) in declaredLookup)
        {
            if (!actualLookup.Contains(key))
            {
                driftRecords.Add(new ManifestDriftRecord
                {
                    Identifier = declared.Id,
                    DriftType = DriftTypes.Missing,
                    DeclaredHash = ComputeSpecHash(declared.Spec)
                });
            }
        }

        return driftRecords;
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }
}
