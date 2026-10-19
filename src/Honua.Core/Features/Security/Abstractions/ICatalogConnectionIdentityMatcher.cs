// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
