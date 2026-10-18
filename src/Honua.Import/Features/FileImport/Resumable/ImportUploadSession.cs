// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Resumable;

// These are internal persistence contracts. HTTP/SDK capability advertisement must
// wait for the authenticated transport, byte verification and import acceptance path.
internal sealed record ImportUploadScope(
    string ActorId, string? TenantId, string TargetId, string WorkspaceId, string TaskId, long TaskRevision);

internal enum ImportUploadState
{
    Receiving,
    Sealed,
    Cancelled
}

internal sealed record ImportUploadSession
{
    public required string Id { get; init; }
    public required ImportUploadScope Scope { get; init; }
    public required string IntentSha256 { get; init; }
    public required string FileName { get; init; }
    public required long TotalBytes { get; init; }
    public required int ChunkBytes { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public long Version { get; init; } = 1;
    public long AcceptedBytes { get; init; }
    public int ChunkCount { get; init; }
    public ImportUploadState State { get; init; }
    public string? ContentSha256 { get; init; }
    public ImportStagedSourceBinding? StagedSource { get; init; }
}

// FileId is a server-generated, unique storage object, never a client object key.
// Its bytes and SHA must be verified before this receipt is appended. UploadId is
// correlation only and is deliberately absent from the object-generation fence.
internal sealed record ImportUploadChunk(
    int Index, long Offset, int Bytes, string Sha256, string FileId, string ETag);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportUploadSession))]
[JsonSerializable(typeof(ImportUploadChunk))]
[JsonSerializable(typeof(ImportUploadScope))]
[JsonSerializable(typeof(ImportStorageReservation))]
[JsonSerializable(typeof(ImportStorageAccount))]
internal sealed partial class ImportUploadJsonContext : JsonSerializerContext;
