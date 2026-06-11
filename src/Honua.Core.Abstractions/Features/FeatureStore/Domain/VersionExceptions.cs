// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Thrown when a branch version cannot be created because a version with the same
/// <c>owner.name</c> identity already exists. Protocol adapters map this to a 409 Conflict
/// response with an Esri-style error so clients can surface a meaningful message
/// ("version already exists") rather than a generic server error.
/// </summary>
public sealed class DuplicateVersionNameException : Exception
{
    /// <summary>The <c>owner.name</c> version identity that conflicted.</summary>
    public string VersionName { get; }

    /// <summary>Initializes the exception for the conflicting version name.</summary>
    /// <param name="versionName">The <c>owner.name</c> identity that already exists.</param>
    public DuplicateVersionNameException(string versionName)
        : base($"A version named '{versionName}' already exists.")
        => VersionName = versionName;

    /// <summary>
    /// Initializes the exception for the conflicting version name, wrapping an underlying
    /// storage-level cause (e.g. a database unique-constraint violation).
    /// </summary>
    /// <param name="versionName">The <c>owner.name</c> identity that already exists.</param>
    /// <param name="innerException">The underlying storage exception.</param>
    public DuplicateVersionNameException(string versionName, Exception innerException)
        : base($"A version named '{versionName}' already exists.", innerException)
        => VersionName = versionName;
}
