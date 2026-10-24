// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Compares a registered provider connection with the catalog database used by
/// imports. Implementations own provider-specific parsing; protocol adapters
/// never interpret connection-string syntax or expose credentials.
/// </summary>
public interface ICatalogConnectionIdentityMatcher
{
    /// <summary>
    /// Returns whether the registered connection targets the same database and
    /// database principal as the configured catalog connection.
    /// </summary>
    bool IsMatch(string provider, string catalogConnectionString, string registeredConnectionString);
}
