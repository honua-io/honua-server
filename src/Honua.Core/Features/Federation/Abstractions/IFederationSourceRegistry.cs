// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

// The registry returns source configuration and never exposes transport credentials.
public interface IFederationSourceRegistry
{
    ImmutableArray<FederatedSourceDescriptor> GetSources();

    bool TryGetSource(string id, out FederatedSourceDescriptor descriptor);
}
