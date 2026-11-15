// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Realtime;

// The fan-out provider when this host has the realtime log, or null on a host without a database.
// The provider is always registered but cannot be built without PostgreSQL, so services built at
// host start read it through this view instead of resolving the provider directly.
public interface IOptionalRealtimeFanout
{
    IRealtimeFanoutProvider? Provider { get; }
}
