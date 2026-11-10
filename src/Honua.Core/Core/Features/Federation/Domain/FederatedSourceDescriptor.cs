// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

// A source descriptor carries no transport credentials.
public sealed record FederatedSourceDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required FederatedSourceKind Kind { get; init; }

    public required Uri Endpoint { get; init; }

    public required string RemoteLayer { get; init; }

    public required TimeSpan RequestTimeout { get; init; }

    public bool Enabled { get; init; } = true;
}
