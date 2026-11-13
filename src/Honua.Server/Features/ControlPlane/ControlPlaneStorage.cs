// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.ControlPlane.Domain;
using System.Data.Common;

namespace Honua.ControlPlane;

// The tables and lease rows every control-plane store reads and writes.
internal sealed record ControlPlaneStorage(JobStoreDatabase Database, ILeaseStore Leases)
{
    public IControlPlaneRealtimeWriter? Realtime { get; init; }
}

internal interface IControlPlaneRealtimeWriter
{
    void NotifyCommitted();
    Task AppendProposalAsync(OperationProposal proposal, ControlPlaneWriteContext context);
    Task AppendTransitionAsync(WorkflowOperationTransition transition, ControlPlaneWriteContext context);
}

internal sealed record ControlPlaneWriteContext(DbTransaction Transaction, CancellationToken CancellationToken);
