// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

// When test schema headers or tenant schema routing are on, multiplexing is disabled and connections reset on pool return so a search_path cannot leak.
public static class RequestScopedSchemaConfiguration
{
    public const string TestSchemaHeadersKey = "HONUA_TEST_SCHEMA_HEADERS";

    public const string TenantSchemaRoutingKey = "MultiTenancy:SchemaRouting:Enabled";

    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool>(TestSchemaHeadersKey)
            || configuration.GetValue<bool>(TenantSchemaRoutingKey);
    }
}
