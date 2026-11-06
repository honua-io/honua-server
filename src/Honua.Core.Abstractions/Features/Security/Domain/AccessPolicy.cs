// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Domain;

public sealed record AccessPolicy
{
    public bool AllowAnonymous { get; init; }

    public bool AllowAnonymousWrite { get; init; }

    public string[]? AllowedRoles { get; init; }

    public string[]? AllowedWriteRoles { get; init; }
}
