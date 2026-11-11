// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.TestKit;

public interface IIsolatedSchemaFixture
{
    NpgsqlDataSource DataSource { get; }

    Task<string> CreateIsolatedSchemaAsync(string testClassName);

    Task DropSchemaAsync(string schemaName);

    Task ExecuteDdlUnderLockAsync(string sql, string? schemaName = null);
}
