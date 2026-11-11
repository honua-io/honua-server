// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public readonly record struct AdmissionSubmissionWindow(
    ExecutionJobKind Kind,
    string RatePrincipal,
    DateTimeOffset Since,
    int CountCap);

// Counts the jobs a rate principal created since the window start, stopping at the cap. The count is
// exact across nodes only while the caller holds the shared submission lease, because the lease keeps
// every node's count-then-create sequence from interleaving.
public interface IAdmissionSubmissionCounter
{
    Task<int> CountAsync(AdmissionSubmissionWindow window, CancellationToken cancellationToken = default);
}
