// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Startup;

internal static class DataStoreBuildPolicy
{
#if HONUA_POSTGIS_ONLY
    public static bool AdditionalStoresIncluded => false;
#else
    public static bool AdditionalStoresIncluded => true;
#endif

    public static void Validate(IConfiguration configuration, bool additionalStoresIncluded)
    {
    }
}
