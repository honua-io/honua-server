// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// The credential registry is a system-database table or an in-process map. A database fault or a
// timeout reaching it is an outage: the caller falls back to the break-glass password and fails
// closed for every other key. Cancellation is the caller's choice, not an outage.
internal static class CredentialStoreFault
{
    internal static bool IsUnavailable(Exception exception)
        => exception is not OperationCanceledException
           && exception is System.Data.Common.DbException or TimeoutException;
}
