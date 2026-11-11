// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

// A metadata release keeps its own retention; every other workflow operation uses the control-plane one.
internal sealed class WorkflowOperationRetention(
    IOptions<ControlPlaneRetentionOptions> controlPlane,
    IOptions<MetadataReleaseOperationOptions> metadataRelease)
{
    public TimeSpan For(WorkflowOperationKind kind)
        => kind == WorkflowOperationKind.MetadataRelease
            ? metadataRelease.Value.OperationRetention
            : controlPlane.Value.WorkflowOperations;
}
