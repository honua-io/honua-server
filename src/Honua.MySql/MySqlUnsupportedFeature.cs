// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.MySql;

internal static class MySqlUnsupportedFeature
{
    public static NotSupportedException Operation(string operation)
        => Create($"Operation '{operation}' is not supported by the MySQL/MariaDB provider.");

    public static NotSupportedException Create(string message)
        => new(message);
}
