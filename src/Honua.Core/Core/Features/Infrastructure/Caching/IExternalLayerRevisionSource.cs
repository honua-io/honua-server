// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Caching;

/// <summary>Reads the committed generation of opt-in external layer edits.</summary>
public interface IExternalLayerRevisionSource
{
    /// <summary>Returns the generation observed by the current request.</summary>
    Task<string> GetRevisionAsync(CancellationToken cancellationToken = default);
}
