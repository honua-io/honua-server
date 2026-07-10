// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Migration;

/// <summary>
/// Configures migration source URL validation guardrails.
/// </summary>
public sealed class MigrationUrlValidationOptions
{
    /// <summary>
    /// Configuration section name for migration URL validation options.
    /// </summary>
    public const string SectionName = "Migration";

    /// <summary>
    /// Host suffixes that migration source URLs must match when configured.
    /// <see langword="null"/> leaves the allowlist unset; an empty array rejects every host.
    /// </summary>
    public string[]? AllowedServiceHostSuffixes { get; set; }

    /// <summary>
    /// Backward-compatible alias for <see cref="AllowedServiceHostSuffixes"/>.
    /// <see langword="null"/> leaves the alias unset; an empty array rejects every host.
    /// </summary>
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
