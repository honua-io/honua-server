// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

public interface IAiIncidentNoticeStore
{
    Task<IReadOnlyList<AiIncidentNotice>> ListPendingIncidentAsync(int limit, CancellationToken cancellationToken);
    Task<bool> MarkQueuedAsync(AiIncidentNotice notice, CancellationToken cancellationToken);
}

public sealed record AiIncidentNotice
{
    public required string DeploymentId { get; init; }
    public required string AuthorityScope { get; init; }
    public required Guid EpisodeId { get; init; }
    public decimal ChargedTokens { get; init; }
    public decimal ChargedMoney { get; init; }
    public long TokenLimit { get; init; }
    public long MoneyLimit { get; init; }
}
