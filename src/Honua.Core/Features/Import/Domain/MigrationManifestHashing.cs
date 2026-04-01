// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Canonical hashing helpers for migration manifests.
/// </summary>
public static class MigrationManifestHasher
{
    /// <summary>
    /// Computes a deterministic SHA-256 hash for a migration manifest.
    /// </summary>
    /// <param name="manifest">Manifest to hash.</param>
    /// <returns>Lowercase SHA-256 hash.</returns>
    public static string ComputeHash(MigrationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var payload = new MigrationManifestHashPayload
        {
            ApiVersion = manifest.ApiVersion,
            TranslatorVersion = manifest.TranslatorVersion,
            SourceType = manifest.SourceType,
            SourceSummary = manifest.SourceSummary,
            Selection = manifest.Selection,
            Summary = manifest.Summary,
            ConnectionDrafts = manifest.ConnectionDrafts,
            PublishPlan = manifest.PublishPlan,
            MetadataResources = manifest.MetadataResources,
            StylePlan = manifest.StylePlan,
            Diagnostics = manifest.Diagnostics
        };

        var element = JsonSerializer.SerializeToElement(
            payload,
            MigrationManifestHashJsonContext.Default.MigrationManifestHashPayload);

        var canonicalJson = CanonicalizeJson(element);
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string CanonicalizeJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonicalJson(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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

internal sealed record MigrationManifestHashPayload
{
    public string ApiVersion { get; init; } = MigrationManifestVersions.V1Alpha1;
    public string TranslatorVersion { get; init; } = string.Empty;
    public MigrationSourceType SourceType { get; init; } = MigrationSourceType.GeoServer;
    public GeoServerMigrationSourceSummary SourceSummary { get; init; } = new();
    public GeoServerMigrationSelection Selection { get; init; } = new();
    public MigrationManifestSummary Summary { get; init; } = new();
    public IReadOnlyList<MigrationConnectionDraft> ConnectionDrafts { get; init; } = [];
    public IReadOnlyList<MigrationPublishPlanEntry> PublishPlan { get; init; } = [];
    public IReadOnlyList<MetadataResource> MetadataResources { get; init; } = [];
    public IReadOnlyList<MigrationStylePlanEntry> StylePlan { get; init; } = [];
    public IReadOnlyList<MigrationDiagnostic> Diagnostics { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MigrationManifestHashPayload))]
internal sealed partial class MigrationManifestHashJsonContext : JsonSerializerContext
{
}
