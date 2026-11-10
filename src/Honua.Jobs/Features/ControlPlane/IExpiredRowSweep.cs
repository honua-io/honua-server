// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

// A store of the system database that deletes its own expired rows: at most batchSize rows per call,
// skipping rows another node has locked. The retention sweep calls it until a batch comes back short.
internal interface IExpiredRowSweep
{
    Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken);
}
