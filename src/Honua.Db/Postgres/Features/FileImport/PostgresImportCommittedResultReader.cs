// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed class PostgresImportCommittedResultReader(
    IAdoNetDatabaseConnectionProvider connections,
    PostgresSchemaConfiguration schemas) : IImportCommittedResultReader
{
    public async Task<ImportResult?> TryReadAsync(string operationId, string intentFingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await PostgresImportCommitReceipt.TryReadAsync(connection, schemas.MetadataSchema,
            operationId, intentFingerprint, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : ImportCommittedResultCodec.Decode(bytes);
    }
}

internal static class ImportCommittedResultCodec
{
    internal static byte[] Encode(ImportResult result)
    {
        Validate(result);
        using var stream = new ReceiptStream();
        JsonSerializer.Serialize(stream, new ImportCommittedResultEnvelope(1, result),
            ImportCommittedResultJsonContext.Default.ImportCommittedResultEnvelope);
        return stream.ToArray();
    }

    internal static ImportResult Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > PostgresImportCommitReceipt.MaximumReceiptBytes)
            throw new InvalidDataException("The committed import result exceeds its bounds.");
        var envelope = JsonSerializer.Deserialize(bytes,
            ImportCommittedResultJsonContext.Default.ImportCommittedResultEnvelope);
        if (envelope is not { Version: 1, Result: { } result })
            throw new InvalidDataException("The committed import result format is invalid.");
        Validate(result);
        return result;
    }

    private static void Validate(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success || result.FeatureCount <= 0 || !Enum.IsDefined(result.Format)
            || string.IsNullOrWhiteSpace(result.TableName) || string.IsNullOrWhiteSpace(result.PhysicalTableName)
            || string.IsNullOrWhiteSpace(result.Schema) || result.Warnings is null || result.ValidationErrors is null)
            throw new InvalidDataException("A committed import result must describe the original successful destination mutation.");
        // Reject oversized input before JSON's own buffering. The bounded output stream
        // additionally counts JSON escaping, property names and structural overhead.
        var remainingCharacters = PostgresImportCommitReceipt.MaximumReceiptBytes;
        void Count(string? value)
        {
            remainingCharacters -= value?.Length ?? 0;
            if (remainingCharacters < 0)
                throw new InvalidDataException("The committed import result exceeds its bounds.");
        }
        Count(result.TableName);
        Count(result.PhysicalTableName);
        Count(result.Schema);
        Count(result.SourceKind);
        Count(result.SourceUrl);
        Count(result.UploadId);
        Count(result.CloudFileId);
        Count(result.ErrorMessage);
        Count(result.ErrorCode);
        if (result.Warnings.Count > 65536 || result.ValidationErrors.Count > 65536)
            throw new InvalidDataException("The committed import diagnostics exceed their bounds.");
        foreach (var warning in result.Warnings) Count(warning);
        foreach (var issue in result.ValidationErrors)
        {
            if (issue is null) throw new InvalidDataException("The committed import diagnostic is invalid.");
            Count(issue.Code);
            Count(issue.Message);
            Count(issue.Field);
        }
    }

    private sealed class ReceiptStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            RequireCapacity(1);
            base.WriteByte(value);
        }

        private void RequireCapacity(int count)
        {
            if (count > PostgresImportCommitReceipt.MaximumReceiptBytes - Length)
                throw new InvalidDataException("The committed import result exceeds its bounds.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ImportCommittedResultEnvelope(int Version, ImportResult Result);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ImportCommittedResultEnvelope))]
internal sealed partial class ImportCommittedResultJsonContext : JsonSerializerContext;
