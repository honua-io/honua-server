// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Assets;

public sealed record WorkTaskAttachment
{
    public required Guid AttachmentId { get; init; }
    public required Guid TaskId { get; init; }
    public required string OwnerSubject { get; init; }
    public required string FieldId { get; init; }
    public required string Filename { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string BlobKey { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string Status { get; init; } = "pending";
}

public interface IWorkTaskAttachmentStore
{
    Task<bool> ReserveAsync(WorkTaskAttachment attachment, WorkTaskAttachmentLimits limits, CancellationToken cancellationToken = default);
    Task<bool> MarkReadyAsync(Guid attachmentId, CancellationToken cancellationToken = default);
    Task<WorkTaskAttachment?> GetAsync(Guid attachmentId, CancellationToken cancellationToken = default);
    Task<WorkTaskAttachment[]> FindReadyAsync(
        (Guid TaskId, string Owner, Guid[] Ids) lookup, CancellationToken cancellationToken = default);
    Task<WorkTaskAttachment[]> ScheduleCleanupAsync(CancellationToken cancellationToken = default);
}

public sealed record WorkTaskAttachmentLimits
{
    public required long ConfigurationGeneration { get; init; }
    public required bool IsPlanner { get; init; }
    public required int MaxCount { get; init; }
    public required int MaxFieldCount { get; init; }
    public required long MaxTotalBytes { get; init; }
}

public sealed record WorkTaskAttachmentResponse(Guid AttachmentId);
