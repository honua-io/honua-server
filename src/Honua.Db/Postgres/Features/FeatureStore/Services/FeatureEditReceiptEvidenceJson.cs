// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class FeatureEditReceiptEvidenceJson
{
    private const int FormatVersion = 1;

    internal static string? Encode(FeatureEditResult result, ReadOnlyMemory<byte> payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteString("payloadSha256", Convert.ToHexString(SHA256.HashData(payload.Span)));
            WriteResults(writer, "creates", result.CreateResults);
            WriteResults(writer, "updates", result.UpdateResults);
            WriteResults(writer, "deletes", result.DeleteResults);
            writer.WriteEndObject();
        }
        return stream.Length <= PostgresGrpcApplyEditsReceipt.MaxReceiptBytes
            ? Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length)) : null;
    }

    internal static FeatureEditReceiptEvidence? Decode(string? json, ReadOnlyMemory<byte> payload)
    {
        if (json is null || json.Length > PostgresGrpcApplyEditsReceipt.MaxReceiptBytes)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != FormatVersion ||
                !string.Equals(root.GetProperty("payloadSha256").GetString(),
                    Convert.ToHexString(SHA256.HashData(payload.Span)), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return new FeatureEditReceiptEvidence
            {
                Creates = ReadResults(root.GetProperty("creates")),
                Updates = ReadResults(root.GetProperty("updates")),
                Deletes = ReadResults(root.GetProperty("deletes"))
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static ImmutableArray<ImmutableArray<FeatureOperationEvidence>> ReadResults(JsonElement results)
        => results.EnumerateArray().Select(result => result.ValueKind == JsonValueKind.Null
            ? default : result.EnumerateArray().Select(FeatureOperationEvidenceJson.Read).ToImmutableArray())
            .ToImmutableArray();

    private static void WriteResults(
        Utf8JsonWriter writer, string name, ImmutableArray<EditOperationResult> results)
    {
        writer.WriteStartArray(name);
        if (!results.IsDefault)
        {
            foreach (var result in results)
            {
                WriteEvidence(writer, result.IsSuccess && !result.IsCommitOutcomeUnknown
                    ? result.OperationEvidence : default);
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, ImmutableArray<FeatureOperationEvidence> evidence)
    {
        if (evidence.IsDefault)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var operation in evidence)
        {
            FeatureOperationEvidenceJson.Write(writer, operation);
        }
        writer.WriteEndArray();
    }
}
