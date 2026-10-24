// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.MySql;

internal static class MySqlUnsupportedFeature
{
    public static NotSupportedException Operation(string operation)
        => Create($"Operation '{operation}' is not supported by the MySQL/MariaDB provider.");

    public static NotSupportedException Create(string message)
        => new(message);
}
