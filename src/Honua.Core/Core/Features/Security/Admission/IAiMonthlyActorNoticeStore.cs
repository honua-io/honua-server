// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

public interface IAiMonthlyActorNoticeStore
{
    Task<IReadOnlyList<AiMonthlyActorNotice>> ListPendingAsync(int limit, CancellationToken cancellationToken);
    Task<bool> MarkQueuedAsync(AiMonthlyActorNotice notice, CancellationToken cancellationToken);
}

public sealed record AiMonthlyActorNotice
{
    public required string DeploymentId { get; init; }
    public required string ActorKey { get; init; }
    public required DateTimeOffset MonthStart { get; init; }
    public decimal ChargedTokens { get; init; }
    public decimal ChargedMoney { get; init; }
    public long TokenLimit { get; init; }
    public long MoneyLimit { get; init; }
}
