// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Snowflake;

public sealed class SnowflakeOptions
{
    public bool Enabled { get; set; } = true;

    public string? ConnectionString { get; set; }

    public string? Account { get; set; }

    public string? User { get; set; }

    public string? Warehouse { get; set; }

    public string? Database { get; set; }

    public string? Schema { get; set; }

    public string? Role { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 60;
}
