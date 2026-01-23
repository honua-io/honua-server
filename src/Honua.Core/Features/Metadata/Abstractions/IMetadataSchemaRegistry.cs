// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Metadata.Schema;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Registry of supported metadata resource schemas and up-conversion rules.
/// </summary>
public interface IMetadataSchemaRegistry
{
    /// <summary>
    /// Gets the current API version supported by the registry.
    /// </summary>
    string CurrentApiVersion { get; }

    /// <summary>
    /// Gets the legacy API version supported for up-conversion.
    /// </summary>
    string LegacyApiVersion { get; }

    /// <summary>
    /// Lists all schema definitions.
    /// </summary>
    IReadOnlyCollection<ResourceSchemaDefinition> Schemas { get; }

    /// <summary>
    /// Validates and up-converts a resource to the current API version when possible.
    /// </summary>
    MetadataSchemaValidationResult ValidateAndUpgrade(MetadataResource resource);

    /// <summary>
    /// Returns the supported API versions for a given kind.
    /// </summary>
    IReadOnlyList<string> GetSupportedApiVersions(string kind);
}
