// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>The committed, deployment-wide configuration generation. Reads must fail when freshness cannot be established.</summary>
public interface IConfigurationGenerationSource
{
    Task<long> GetGenerationAsync(CancellationToken cancellationToken);
}
