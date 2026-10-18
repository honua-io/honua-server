// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.AgentWorkspace;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateDatasetImportUpload([property: JsonRequired] string TaskContentHash);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MutateDatasetImportUpload([property: JsonRequired] string TaskContentHash,
    [property: JsonRequired] long ExpectedVersion);

// Public transport projection only. No provider object key, ETag, path, signed URL,
// credential snapshot or policy claims leave the internal coordinator contracts.
internal sealed record DatasetImportUploadReceipt(string TaskId, long TaskRevision, string TaskContentHash,
    string UploadId, long Version, string State, long AcceptedBytes, long TotalBytes, int ChunkBytes,
    int ChunkCount, DateTimeOffset ExpiresAt, string? ContentSha256, DatasetImportSourceRetentionReceipt? SourceRetention);

internal sealed record DatasetImportSourceRetentionReceipt(long Bytes, string Sha256, DateTimeOffset? ExpiresAt);

// This describes transfer only, never canonical import acceptance or a prepared job.
internal sealed record DatasetImportUploadCapabilities(int ChunkBytes, long MaximumFileBytes,
    bool ImportAcceptanceSupported = false);

// Explicit host-installed configuration is required before these routes are mapped.
// There is deliberately no request field for a path, scratch allowance or budget.
internal sealed record DatasetImportUploadTransportOptions(string ScratchDirectory,
    long MaximumScratchBytes, long MaximumFileBytes)
{
    internal const int MaximumChunkBytes = 4 * 1024 * 1024;
    internal const long MaximumChunkedFileBytes = (long)MaximumChunkBytes * 65536;
    internal long EffectiveMaximumFileBytes => Math.Min(MaximumFileBytes,
        Math.Min(MaximumScratchBytes, MaximumChunkedFileBytes));

    internal bool IsConfigured => Path.IsPathFullyQualified(ScratchDirectory) && MaximumScratchBytes > 0 && MaximumFileBytes > 0;
}

// Endpoint metadata permits the canonical bounded chunk body through the shared
// workspace guard without allowing larger JSON on any other workspace endpoint.
internal sealed record DatasetImportChunkBodyLimit;
