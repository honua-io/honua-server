// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

public enum ImportLoadMode
{
    // Full replace swaps a staging table so a failed or cancelled load never leaves a half-dropped target.
    Replace = 0,

    Append = 1,

    Upsert = 2,
}

public sealed class ImportCommitScope
{
    public required object Transaction { get; init; }

    public required string Schema { get; init; }

    public required string PhysicalTable { get; init; }

    public required int FeatureCount { get; init; }
}

public delegate ValueTask ImportCommitParticipant(ImportCommitScope scope, CancellationToken cancellationToken);

public sealed record ImportRequest
{
    public string? OperationInstanceId { get; init; }

    // The correlation identity is canonical and is never derived from job metadata.
    public string? CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public Stream? FileStream { get; init; }

    public string? CloudFileId { get; init; }

    public string? LocalFilePath { get; init; }

    public string? ObjectStoreUri { get; init; }

    public string? ObjectStoreETag { get; init; }

    public string? ObjectStoreRoleArn { get; init; }

    public string? ObjectStoreContentType { get; init; }

    public required string FileName { get; init; }

    public string SourceKind { get; init; } = "file";

    public string? SourceUrl { get; init; }

    public string? UploadId { get; init; }

    public required string TableName { get; init; }

    public string? TargetSchema { get; init; }

    public int? SourceSrid { get; init; }

    public int TargetSrid { get; init; } = 4326;

    // When the mode is left at its default, overwrite selects replace and otherwise the load appends, so an existing target is not silently destroyed.
    public bool OverwriteExisting { get; init; }

    public ImportLoadMode LoadMode { get; init; } = ImportLoadMode.Replace;

    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public CsvImportOptions? CsvOptions { get; init; }

    // A direct append into a table the importer does not own is refused when a commit participant is set, because those batches commit on their own.
    public ImportCommitParticipant? CommitParticipant { get; init; }

    public ImportLoadMode EffectiveLoadMode =>
        LoadMode != ImportLoadMode.Replace
            ? LoadMode
            : OverwriteExisting ? ImportLoadMode.Replace : ImportLoadMode.Append;

    public void Validate()
    {
        var cloudFileId = string.IsNullOrWhiteSpace(CloudFileId) ? null : CloudFileId;
        var localFilePath = string.IsNullOrWhiteSpace(LocalFilePath) ? null : LocalFilePath;
        var objectStoreUri = string.IsNullOrWhiteSpace(ObjectStoreUri) ? null : ObjectStoreUri;
        var sourceCount = 0;

        if (FileStream != null)
        {
            sourceCount++;
        }

        if (cloudFileId != null)
        {
            sourceCount++;
        }

        if (localFilePath != null)
        {
            sourceCount++;
        }

        if (objectStoreUri != null)
        {
            sourceCount++;
        }

        if (sourceCount == 0)
        {
            throw new InvalidOperationException("Either FileStream, CloudFileId, LocalFilePath, or ObjectStoreUri must be provided.");
        }

        if (sourceCount > 1)
        {
            throw new InvalidOperationException("Only one of FileStream, CloudFileId, LocalFilePath, or ObjectStoreUri should be provided, not multiple sources.");
        }

        if (EffectiveLoadMode == ImportLoadMode.Upsert && KeyColumns.Count == 0)
        {
            throw new InvalidOperationException("Upsert load mode requires at least one key column.");
        }
    }

    public bool UsesCloudStorage => !string.IsNullOrWhiteSpace(CloudFileId);

    public bool UsesLocalFile => !string.IsNullOrWhiteSpace(LocalFilePath);

    public bool UsesObjectStore => !string.IsNullOrWhiteSpace(ObjectStoreUri);
}
