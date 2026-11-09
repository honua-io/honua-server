// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Core.Features.EmbedGovernance.Abstractions;

public sealed record EmbedKeyCreateResult(EmbedKeyRecord Record, string Key);

public sealed record EmbedKeyValidationResult(EmbedKeyRecord Record);

public interface IEmbedKeyStore
{
    Task<IReadOnlyList<EmbedKeyRecord>> ListAsync(CancellationToken cancellationToken);

    Task<EmbedKeyCreateResult> CreateAsync(
        string name,
        EmbedKeyScope scope,
        DateTimeOffset? expiresAt,
        string? createdBy,
        CancellationToken cancellationToken);

    Task<EmbedKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<EmbedKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken);

    Task<EmbedKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken);

    Task<EmbedKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken);

    Task<int> RecordRequestAsync(Guid id, TimeSpan window, CancellationToken cancellationToken);
}
