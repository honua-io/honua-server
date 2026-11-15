// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class AwsEcsAlbDeployBackend
{
    public Task EnsurePreparationAdmittedAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
        => EnsureAlarmAdmissionAsync(
            operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is missing."), cancellationToken);

    private async Task EnsureAlarmAdmissionAsync(DeployOperationSpec spec, CancellationToken cancellationToken)
    {
        if (AlarmAdmission is not null &&
            await AlarmAdmission.GetBlockingReasonAsync(spec.TargetId, cancellationToken).ConfigureAwait(false) is { } reason)
        {
            throw new DeployAdmissionBlockedException(reason);
        }
    }
}
