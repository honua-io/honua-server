// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;

namespace Honua.Core.Features.Configuration;

[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "TTL categories are domain terms used throughout configuration and docs.")]
public enum TtlCategory
{
    VeryShort,

    Short,

    Medium,

    Long,

    VeryLong
}

[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Public configuration property names intentionally mirror TTL category names.")]
public sealed class StandardTtlOptions
{
    public const string SectionName = "StandardTtl";

    public StandardTtlOptions()
    {
        ApplyDefaults(isDevelopment: false);
    }

    public StandardTtlOptions(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ApplyDefaults(environment.IsDevelopment());
    }

    public TimeSpan VeryShort { get; set; }

    public TimeSpan Short { get; set; }

    public TimeSpan Medium { get; set; }

    public TimeSpan Long { get; set; }

    public TimeSpan VeryLong { get; set; }

    public TimeSpan GetTtl(TtlCategory category) => category switch
    {
        TtlCategory.VeryShort => VeryShort,
        TtlCategory.Short => Short,
        TtlCategory.Medium => Medium,
        TtlCategory.Long => Long,
        TtlCategory.VeryLong => VeryLong,
        _ => Medium
    };

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

public static class TtlCategoryExtensions
{
    public static string GetDescription(this TtlCategory category) => category switch
    {
        TtlCategory.VeryShort => "Frequently changing data (user sessions, real-time metrics)",
        TtlCategory.Short => "Semi-static data (layer configurations, service metadata)",
        TtlCategory.Medium => "Stable data (feature schemas, service capabilities)",
        TtlCategory.Long => "Rarely changing data (coordinate systems, static configurations)",
        TtlCategory.VeryLong => "Immutable data (tile matrix sets, projection definitions)",
        _ => "Stable data (feature schemas, service capabilities)"
    };

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
