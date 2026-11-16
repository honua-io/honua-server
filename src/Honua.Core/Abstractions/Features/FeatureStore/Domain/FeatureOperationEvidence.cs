// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct FeatureOperationEvidence
{
    public required long Generation { get; init; }
    public required int LayerId { get; init; }
    public required long ObjectId { get; init; }
    public long? PublicObjectId { get; init; }
    public Guid? VersionId { get; init; }
    public required FeatureChangeOperation Operation { get; init; }
}
