// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// A frozen Lambda keeps pooled connections open because pool pruning does not run while it is frozen, so each invocation releases them.
public interface IDatabaseConnectionReleaser
{
    ValueTask ReleasePooledConnectionsAsync(CancellationToken cancellationToken = default);
}
