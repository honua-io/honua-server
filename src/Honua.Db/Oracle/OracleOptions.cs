// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Oracle;

// Oracle.ManagedDataAccess.Core reflects internally and is not Native AOT compatible, so an AOT publish must leave this provider out.
public sealed class OracleOptions
{
    public string? ConnectionString { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 60;
}
