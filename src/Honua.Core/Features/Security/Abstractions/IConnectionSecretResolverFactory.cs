// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Factory for creating connection secret resolver instances.
/// </summary>
public interface IConnectionSecretResolverFactory
{
    /// <summary>
    /// Creates a secret resolver for the specified provider type.
    /// </summary>
    /// <param name="providerType">The provider type (e.g., "aws", "azure", "env")</param>
    /// <returns>A secret resolver instance</returns>
    IConnectionSecretResolver CreateResolver(string providerType);
}
