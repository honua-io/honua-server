// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

public interface IEsriConstructCapabilityRegistry
{
    /// <summary>
    /// Returns the registered descriptor for <paramref name="constructKey"/>,
    /// or the <c>manual-review</c> fallback descriptor when the key is unknown.
    /// </summary>
    /// <param name="constructKey">Registry lookup key.</param>
    GeoServicesConstructCapabilityDescriptor ResolveOrUnknown(string constructKey);

    /// <summary>
    /// Construct keys with a registered descriptor. The unknown fallback is excluded.
    /// </summary>
    IReadOnlyCollection<string> RegisteredConstructKeys { get; }
}
