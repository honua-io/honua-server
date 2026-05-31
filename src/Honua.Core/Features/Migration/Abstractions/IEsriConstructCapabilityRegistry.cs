// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Registry that resolves an <see cref="EsriConstructCapabilityDescriptor"/>
/// for a given Esri construct category key. Misses fall back to a safe
/// <c>manual-review</c> descriptor so callers can stay branch-free.
/// </summary>
/// <remarks>
/// The registry is the single source of construct tier metadata consulted by
/// import fidelity classification and exposed to the facade, GP, and reporting
/// lanes via dependency injection. Adding a new Esri construct is one new
/// descriptor entry plus any runtime condition the classifier needs.
/// </remarks>
public interface IEsriConstructCapabilityRegistry
{
    /// <summary>
    /// Returns the registered descriptor for <paramref name="constructKey"/>,
    /// or the <c>manual-review</c> fallback descriptor when the key is unknown.
    /// </summary>
    /// <param name="constructKey">Registry lookup key.</param>
    EsriConstructCapabilityDescriptor ResolveOrUnknown(string constructKey);

    /// <summary>
    /// Construct keys with a registered descriptor. The unknown fallback is excluded.
    /// </summary>
    IReadOnlyCollection<string> RegisteredConstructKeys { get; }
}
