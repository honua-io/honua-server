// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct FeatureChange
{
    public required long ChangeId { get; init; }

    public required long Generation { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public long? PublicObjectId { get; init; }

    public string? OriginReplicaId { get; init; }

    // A null pre-image means the window starts with an insert or predates image capture, so a held row cannot be told from one the client has not received.
    public long? PreImageChangeId { get; init; }

    public required FeatureChangeOperation Operation { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }
}

public enum FeatureChangeOperation : short
{
    Insert = 1,

    Update = 2,

    Delete = 3
}
