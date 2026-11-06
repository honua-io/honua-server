// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Abstractions;

// An executor that does not implement this marker keeps the synthetic completed result, so a dry-run flag cannot actuate it.
public interface IOperationDryRunExecutor : IOperationExecutor
{
    bool ExecutesDryRun => true;
}
