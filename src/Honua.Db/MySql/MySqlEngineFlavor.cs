// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.MySql;

public enum MySqlEngineFlavor
{
    Mysql = 0,

    MariaDb = 1
}

internal sealed class MySqlEngineFlavorHolder
{
    public MySqlEngineFlavorHolder(MySqlEngineFlavor flavor) => Flavor = flavor;

    public MySqlEngineFlavor Flavor { get; }
}
