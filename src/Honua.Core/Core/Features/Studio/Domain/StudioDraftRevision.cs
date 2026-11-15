// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Domain;

public sealed record StudioDraftRevision
{
    public required long Generation { get; init; }

    public required StudioPackageEnvelope Envelope { get; init; }

    public string? UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record StudioDraftRevisionSummary(long Generation, string? UpdatedBy, DateTimeOffset UpdatedAt);

public sealed record StudioDraftRevisionQuery(Guid DraftId, int Limit = 25, long? BeforeGeneration = null);
