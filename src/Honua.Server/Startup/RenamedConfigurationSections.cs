// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Server.Startup;

internal static class RenamedConfigurationSections
{
    private static readonly (string Retired, string Current)[] Renames = [("ArcGisRest", "GeoServicesRest")];

    public static void ThrowIfRetiredSectionPresent(IConfiguration configuration)
    {
        foreach (var (retired, current) in Renames)
        {
            if (configuration.GetSection(retired).Exists())
            {
                throw new InvalidOperationException(
                    $"The '{retired}' configuration section was renamed to '{current}'. Rename every '{retired}:*' "
                    + $"setting (environment variables '{retired}__*') to '{current}' and restart.");
            }
        }
    }
}
