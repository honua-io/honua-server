// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// A database or timeout fault means the credential store is down, so callers fall back to the
// break-glass comparison. Cancellation is not an outage.
internal static class CredentialStoreFault
{
    internal static bool IsUnavailable(Exception exception)
        => exception is not OperationCanceledException
           && exception is System.Data.Common.DbException or TimeoutException;
}
