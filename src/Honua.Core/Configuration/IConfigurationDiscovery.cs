// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Configuration;

/// <summary>
/// Provides metadata discovery for registered configuration option types.
/// </summary>
public interface IConfigurationDiscovery
{
    /// <summary>
    /// Returns metadata for all registered configuration option types.
    /// </summary>
    /// <returns>The registered configuration metadata.</returns>
    IEnumerable<ConfigurationOptionsMetadata> GetAllOptions();

    /// <summary>
    /// Returns metadata for a single registered configuration option type.
    /// </summary>
    /// <typeparam name="TOptions">The registered options type.</typeparam>
    /// <returns>The configuration metadata.</returns>
    ConfigurationOptionsMetadata GetOptionsMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>()
        where TOptions : class;
}
