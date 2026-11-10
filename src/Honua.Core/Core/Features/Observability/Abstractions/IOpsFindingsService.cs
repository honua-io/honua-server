// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IOpsFindingsService
{
    Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default);

    // A provisioning root that disagrees with the finding's linked workflow operation is rejected.
    Task<OpsFindingProposalResult> ProposeAsync(
        string findingId,
        string? rootProvisioningOperationId = null,
        CancellationToken cancellationToken = default);
}
