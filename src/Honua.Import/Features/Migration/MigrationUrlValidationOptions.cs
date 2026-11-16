// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Migration;

public sealed class MigrationUrlValidationOptions
{
    public const string SectionName = "Migration";

    public string[]? AllowedServiceHostSuffixes { get; set; }

    public string[]? AllowedHostSuffixes { get; set; }

    internal IReadOnlyCollection<string>? ResolveAllowedServiceHostSuffixes()
    {
        if (AllowedServiceHostSuffixes is not null)
        {
            return AllowedServiceHostSuffixes;
        }

        return AllowedHostSuffixes;
    }
}

internal static class MigrationUrlValidationOptionsExtensions
{
    public static IReadOnlyCollection<string>? GetAllowedMigrationServiceHostSuffixes(this IServiceProvider services)
        => services.GetService<IOptions<MigrationUrlValidationOptions>>()?.Value.ResolveAllowedServiceHostSuffixes();
}
