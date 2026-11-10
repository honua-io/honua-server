// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public static class DatabasePreflightCauseClasses
{
    public const string ConnectionBorrowTimeout = "connection-borrow-timeout";

    public const string ConnectionRefused = "connection-refused";

    public const string Auth = "auth";

    public const string ConnectionUnavailable = "connection-unavailable";

    public const string IncompatibleDatabase = "incompatible-database";

    public static bool IsDegradable(string? causeClass)
        => causeClass is ConnectionBorrowTimeout or ConnectionRefused or Auth or ConnectionUnavailable;
}
