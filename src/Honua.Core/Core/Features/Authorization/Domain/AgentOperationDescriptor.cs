// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public sealed record AgentOperationDescriptor
{
    public required string OperationId { get; init; }

    public string? Protocol { get; init; }

    public bool IsMutating { get; init; }

    public bool IsDestructive { get; init; }

    public bool SupportsSnapshot { get; init; } = true;
}
