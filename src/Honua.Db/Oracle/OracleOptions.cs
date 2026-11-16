// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Oracle;

public sealed class OracleOptions
{
    public string? ConnectionString { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 60;
}
