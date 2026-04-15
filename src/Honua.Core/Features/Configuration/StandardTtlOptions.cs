// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;

namespace Honua.Core.Features.Configuration;

/// <summary>
/// Standard TTL categories used across the application.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Short and long TTL names are stable public configuration categories.")]
public enum TtlCategory
{
    /// <summary>
    /// TTL for highly volatile data.
    /// </summary>
    VeryShort,

    /// <summary>
    /// TTL for semi-static data.
    /// </summary>
    Short,

    /// <summary>
    /// TTL for stable data.
    /// </summary>
    Medium,

    /// <summary>
    /// TTL for rarely changing data.
    /// </summary>
    Long,

    /// <summary>
    /// TTL for effectively immutable data.
    /// </summary>
    VeryLong
}

/// <summary>
/// Provides standardized cache TTL values with environment-aware defaults.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Short and long TTL property names are part of the public configuration contract.")]
public sealed class StandardTtlOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "StandardTtl";

    /// <summary>
    /// Initializes a new instance of the <see cref="StandardTtlOptions"/> class.
    /// </summary>
    public StandardTtlOptions()
    {
        ApplyDefaults(isDevelopment: false);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StandardTtlOptions"/> class using the current environment.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    public StandardTtlOptions(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ApplyDefaults(environment.IsDevelopment());
    }

    /// <summary>
    /// Gets or sets the very short TTL.
    /// </summary>
    public TimeSpan VeryShort { get; set; }

    /// <summary>
    /// Gets or sets the short TTL.
    /// </summary>
    public TimeSpan Short { get; set; }

    /// <summary>
    /// Gets or sets the medium TTL.
    /// </summary>
    public TimeSpan Medium { get; set; }

    /// <summary>
    /// Gets or sets the long TTL.
    /// </summary>
    public TimeSpan Long { get; set; }

    /// <summary>
    /// Gets or sets the very long TTL.
    /// </summary>
    public TimeSpan VeryLong { get; set; }

    /// <summary>
    /// Returns the configured TTL for the requested category.
    /// </summary>
    /// <param name="category">The TTL category.</param>
    /// <returns>The configured TTL.</returns>
    public TimeSpan GetTtl(TtlCategory category) => category switch
    {
        TtlCategory.VeryShort => VeryShort,
        TtlCategory.Short => Short,
        TtlCategory.Medium => Medium,
        TtlCategory.Long => Long,
        TtlCategory.VeryLong => VeryLong,
        _ => Medium
    };

    /// <summary>
    /// Validates that all TTL values are positive and ordered from shortest to longest.
    /// </summary>
    /// <returns>True when the TTL values are valid.</returns>
    public bool ValidateTtlOrdering()
    {
        if (VeryShort <= TimeSpan.Zero ||
            Short <= TimeSpan.Zero ||
            Medium <= TimeSpan.Zero ||
            Long <= TimeSpan.Zero ||
            VeryLong <= TimeSpan.Zero)
        {
            return false;
        }

        return VeryShort <= Short &&
               Short <= Medium &&
               Medium <= Long &&
               Long <= VeryLong;
    }

    /// <summary>
    /// Returns the configured TTL values keyed by category name.
    /// </summary>
    /// <returns>The configured TTL map.</returns>
    public IReadOnlyDictionary<string, TimeSpan> GetAllTtls() => new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [nameof(VeryShort)] = VeryShort,
        [nameof(Short)] = Short,
        [nameof(Medium)] = Medium,
        [nameof(Long)] = Long,
        [nameof(VeryLong)] = VeryLong
    };

    private void ApplyDefaults(bool isDevelopment)
    {
        if (isDevelopment)
        {
            VeryShort = TimeSpan.FromSeconds(30);
            Short = TimeSpan.FromMinutes(2);
            Medium = TimeSpan.FromMinutes(5);
            Long = TimeSpan.FromMinutes(30);
            VeryLong = TimeSpan.FromHours(2);
            return;
        }

        VeryShort = TimeSpan.FromMinutes(2);
        Short = TimeSpan.FromMinutes(5);
        Medium = TimeSpan.FromMinutes(30);
        Long = TimeSpan.FromHours(2);
        VeryLong = TimeSpan.FromDays(1);
    }
}

/// <summary>
/// Extension helpers for <see cref="TtlCategory"/>.
/// </summary>
public static class TtlCategoryExtensions
{
    /// <summary>
    /// Gets a human-readable description for a TTL category.
    /// </summary>
    /// <param name="category">The TTL category.</param>
    /// <returns>The category description.</returns>
    public static string GetDescription(this TtlCategory category) => category switch
    {
        TtlCategory.VeryShort => "Frequently changing data (user sessions, real-time metrics)",
        TtlCategory.Short => "Semi-static data (layer configurations, service metadata)",
        TtlCategory.Medium => "Stable data (feature schemas, service capabilities)",
        TtlCategory.Long => "Rarely changing data (coordinate systems, static configurations)",
        TtlCategory.VeryLong => "Immutable data (tile matrix sets, projection definitions)",
        _ => "Stable data (feature schemas, service capabilities)"
    };

    /// <summary>
    /// Recommends a TTL category for a conceptual data type.
    /// </summary>
    /// <param name="dataType">The data type hint.</param>
    /// <returns>The recommended TTL category.</returns>
    public static TtlCategory GetRecommendedCategory(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return TtlCategory.Medium;
        }

        return dataType.Trim().ToLowerInvariant() switch
        {
            var value when value.Contains("session", StringComparison.Ordinal) => TtlCategory.VeryShort,
            var value when value.Contains("metadata", StringComparison.Ordinal) => TtlCategory.Short,
            var value when value.Contains("schema", StringComparison.Ordinal) => TtlCategory.Medium,
            var value when value.Contains("coordinate", StringComparison.Ordinal) => TtlCategory.Long,
            var value when value.Contains("tilematrix", StringComparison.Ordinal) => TtlCategory.VeryLong,
            _ => TtlCategory.Medium
        };
    }
}
