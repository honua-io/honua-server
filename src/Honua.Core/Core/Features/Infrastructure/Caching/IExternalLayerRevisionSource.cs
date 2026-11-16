// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Caching;

public interface IExternalLayerRevisionSource
{
    Task<string> GetRevisionAsync(CancellationToken cancellationToken = default);
}
