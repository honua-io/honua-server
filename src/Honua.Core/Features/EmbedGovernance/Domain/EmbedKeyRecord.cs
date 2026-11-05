// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

public enum EmbedKeyStatus
{
    Active = 0,

    Expired = 1,

    Revoked = 2,
}

public sealed record EmbedKeyRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string KeyPrefix { get; init; }

    public required byte[] KeyHash { get; init; }

    public required EmbedKeyScope Scope { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public DateTimeOffset? RotatedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public string? CreatedBy { get; init; }

    public EmbedKeyStatus GetStatus(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return EmbedKeyStatus.Revoked;
        }

        if (ExpiresAt.HasValue && ExpiresAt.Value <= now)
        {
            return EmbedKeyStatus.Expired;
        }

        return EmbedKeyStatus.Active;
    }
}
