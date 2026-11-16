// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed class WorkspaceQuotaExceededException : InvalidOperationException
{
    public WorkspaceQuotaExceededException() : base("The active workspace count limit has been reached.")
    {
    }
    public WorkspaceQuotaExceededException(string message) : base(message)
    {
    }
}
