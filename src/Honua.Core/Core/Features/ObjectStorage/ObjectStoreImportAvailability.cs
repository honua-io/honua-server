// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Import.ObjectStore;

namespace Honua.Core.Features.ObjectStorage;

// Object storage import is configured only when this host can read S3 and at least one connection exists,
// built in or created by an operator. An install with neither does not advertise it.
public sealed class ObjectStoreImportAvailability(
    IObjectStorageConnectionResolver connections,
    IImportObjectStoreReader? reader = null)
{
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            return false;
        }

        try
        {
            return (await connections.ListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;
        }
        catch (DbException)
        {
            // An unreadable connection table reports the capability as unconfigured rather than failing the caller.
            return false;
        }
    }
}
