// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

public sealed class WorkflowPackageGraphConflictException : InvalidOperationException
{
    public WorkflowPackageGraphConflictException(string packageId)
        : base($"Workflow package '{packageId}' changed after validation. Retry version creation.")
    {
        PackageId = packageId;
    }

    public string PackageId { get; }
}
