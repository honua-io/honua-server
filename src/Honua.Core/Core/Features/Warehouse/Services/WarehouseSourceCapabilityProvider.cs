// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Domain;

namespace Honua.Core.Features.Warehouse.Services;

public sealed class WarehouseRuntimeAvailabilityState
{
    private readonly FrozenDictionary<string, (bool Enabled, string? Reason)> _states;

    public WarehouseRuntimeAvailabilityState(
        IEnumerable<KeyValuePair<string, (bool Enabled, string? Reason)>> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _states = states.ToFrozenDictionary(
            static pair => DataProviderNames.Normalize(pair.Key),
            static pair => pair.Value,
            StringComparer.Ordinal);
    }

    public static WarehouseRuntimeAvailabilityState Empty { get; } =
        new(Array.Empty<KeyValuePair<string, (bool, string?)>>());

    public bool TryGetState(string providerName, out (bool Enabled, string? Reason) state)
        => _states.TryGetValue(DataProviderNames.Normalize(providerName), out state);
}

public sealed class WarehouseSourceCapabilityProvider : IWarehouseSourceCapabilityProvider
{
    public static readonly ImmutableArray<string> CanonicalSupportedProviders =
    [
        DataProviderNames.Redshift,
        DataProviderNames.Snowflake,
        DataProviderNames.Databricks,
        DataProviderNames.DuckDb,
    ];

    private static readonly FrozenSet<string> SupportedProviderSet =
        CanonicalSupportedProviders.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> HttpFsSchemes = new[]
    {
        "http", "https", "s3", "s3a", "s3n", "gs", "gcs", "r2"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> AzureSchemes = new[]
    {
        "az", "azure", "abfs", "abfss", "wasb", "wasbs"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<string, IWarehouseProviderCapabilityDescriptor> _descriptors;
    private readonly WarehouseRuntimeAvailabilityState _runtimeState;

    public WarehouseSourceCapabilityProvider(
        IEnumerable<IWarehouseProviderCapabilityDescriptor>? descriptors = null,
        WarehouseRuntimeAvailabilityState? runtimeState = null)
    {
        var map = new Dictionary<string, IWarehouseProviderCapabilityDescriptor>(StringComparer.Ordinal);
        if (descriptors is not null)
        {
            foreach (var descriptor in descriptors)
            {
                var normalized = DataProviderNames.Normalize(descriptor.ProviderName);
                if (SupportedProviderSet.Contains(normalized))
                {
                    map[normalized] = descriptor;
                }
            }
        }

        _descriptors = map.ToFrozenDictionary(StringComparer.Ordinal);
        _runtimeState = runtimeState ?? WarehouseRuntimeAvailabilityState.Empty;
    }

    public IReadOnlyCollection<string> SupportedProviders => CanonicalSupportedProviders;

    public Task<WarehouseEffectiveSourceCapabilities> GetCapabilitiesAsync(
        WarehouseSourceCapabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedProvider = NormalizeProvider(request.ProviderName);
        var safeSourceId = SanitizeIdentifier(request.SourceId);
        if (!SupportedProviderSet.Contains(normalizedProvider))
        {
            return Task.FromResult(BuildUnsupportedCapabilities(normalizedProvider, safeSourceId));
        }

        var safeRequest = request with
        {
            ProviderName = normalizedProvider,
            SourceId = safeSourceId,
            UnavailableReason = SanitizeDiagnostic(request.UnavailableReason),
        };

        var baseline = _descriptors.TryGetValue(normalizedProvider, out var descriptor)
            ? descriptor.Describe(safeRequest)
            : BuildBaselineCapabilities(normalizedProvider, safeRequest);

        var (availability, reason) = ResolveAvailability(normalizedProvider, safeRequest, baseline);
        var effective = baseline with
        {
            ProviderName = normalizedProvider,
            SourceId = safeSourceId,
            Availability = availability,
            AvailabilityReason = SanitizeDiagnostic(reason),
            ReadOnly = true,
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(effective);
    }

    public async Task<IReadOnlyList<WarehouseEffectiveSourceCapabilities>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<WarehouseEffectiveSourceCapabilities>(CanonicalSupportedProviders.Length);
        foreach (var provider in CanonicalSupportedProviders)
        {
            var caps = await GetCapabilitiesAsync(
                new WarehouseSourceCapabilityRequest
                {
                    ProviderName = provider,
                    SourceId = $"default:{provider}",
                },
                cancellationToken).ConfigureAwait(false);
            results.Add(caps);
        }

        return results;
    }

    public static WarehouseEffectiveSourceCapabilities BuildBaselineCapabilities(
        string providerName,
        WarehouseSourceCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = NormalizeProvider(providerName);
        var safeSourceId = SanitizeIdentifier(request.SourceId);
        return normalized switch
        {
            DataProviderNames.DuckDb => BuildDuckDbCapabilities(safeSourceId, request),
            DataProviderNames.Snowflake => BuildSnowflakeCapabilities(safeSourceId),
            DataProviderNames.Databricks => BuildDatabricksCapabilities(safeSourceId),
            DataProviderNames.Redshift => BuildRedshiftCapabilities(safeSourceId),
            _ => BuildUnsupportedCapabilities(normalized, safeSourceId),
        };
    }

    public static ImmutableArray<string> ResolveDuckDbExtensions(string? externalSourceUri, bool includeH3 = false)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        builder.Add("spatial");

        if (TryExtractScheme(externalSourceUri, out var scheme))
        {
            if (HttpFsSchemes.Contains(scheme))
            {
                builder.Add("httpfs");
            }
            else if (AzureSchemes.Contains(scheme))
            {
                builder.Add("azure");
            }
        }

        if (includeH3)
        {
            builder.Add("h3");
        }

        return builder.ToImmutable();
    }

    public static string SanitizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var trimmed = value.Trim();
        var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex];
        }

        return ProviderErrorBodyRedactor.Redact(trimmed, 256);
    }

    public static string? SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var redacted = ProviderErrorBodyRedactor.Redact(value, 256);
        return string.IsNullOrWhiteSpace(redacted) ? null : redacted;
    }

    private (WarehouseSourceAvailability Availability, string? Reason) ResolveAvailability(
        string providerName,
        WarehouseSourceCapabilityRequest request,
        WarehouseEffectiveSourceCapabilities baseline)
    {
        if (request.RuntimeEnabled is false)
        {
            return (WarehouseSourceAvailability.Disabled, request.UnavailableReason ?? $"Provider '{providerName}' is disabled.");
        }

        if (request.RuntimeEnabled is null
            && _runtimeState.TryGetState(providerName, out var state)
            && !state.Enabled)
        {
            return (WarehouseSourceAvailability.Disabled, state.Reason ?? $"Provider '{providerName}' is disabled in runtime configuration.");
        }

        if (request.RuntimeReachable is false)
        {
            return (WarehouseSourceAvailability.Unavailable, request.UnavailableReason ?? $"Provider '{providerName}' is currently unreachable.");
        }

        if (baseline.Availability != WarehouseSourceAvailability.Available)
        {
            return (baseline.Availability, request.UnavailableReason ?? baseline.AvailabilityReason);
        }

        return (WarehouseSourceAvailability.Available, null);
    }

    private static WarehouseEffectiveSourceCapabilities BuildDuckDbCapabilities(
        string sourceId,
        WarehouseSourceCapabilityRequest request)
    {
        var extensions = ResolveDuckDbExtensions(request.ExternalSourceUri, includeH3: false);
        var h3Extensions = ResolveDuckDbExtensions(request.ExternalSourceUri, includeH3: true);
        return new WarehouseEffectiveSourceCapabilities
        {
            ProviderName = DataProviderNames.DuckDb,
            SourceId = sourceId,
            Availability = WarehouseSourceAvailability.Available,
            ReadOnly = true,
            SupportsBboxPushdown = true,
            SupportsTemporalFilter = true,
            SupportsSqlFilterPushdown = true,
            SupportsEnforcedPolicyPushdown = true,
            SupportedSpatialPredicates =
            [
                SpatialRelationship.Intersects,
                SpatialRelationship.Within,
                SpatialRelationship.Contains,
                SpatialRelationship.EnvelopeIntersects,
                SpatialRelationship.Crosses,
                SpatialRelationship.Touches,
                SpatialRelationship.Overlaps,
                SpatialRelationship.Disjoint,
                SpatialRelationship.Equals,
                SpatialRelationship.WithinDistance,
            ],
            CrsBehavior = new WarehouseCrsBehavior
            {
                SupportsCrossSridFilter = true,
                SupportsOutputReprojection = true,
                AssumesWgs84WhenUnset = true,
                AxisOrder = "xy",
                Notes = "Supports ST_Transform with always_xy := true and geodesic spheroid distance for WGS 84 geographic SRIDs.",
            },
            Aggregation = new WarehouseAggregationCapabilities
            {
                SupportsStatistics = true,
                SupportsGroupBy = true,
                SupportsHaving = false,
                SupportsH3Aggregation = true,
                SupportsH3Polyfill = true,
                SupportsH3KRing = true,
                RequiredExtensions = h3Extensions,
            },
            RequiredExtensions = extensions,
        };
    }

    private static WarehouseEffectiveSourceCapabilities BuildSnowflakeCapabilities(string sourceId)
        => new()
        {
            ProviderName = DataProviderNames.Snowflake,
            SourceId = sourceId,
            Availability = WarehouseSourceAvailability.Available,
            ReadOnly = true,
            SupportsBboxPushdown = true,
            SupportsTemporalFilter = false,
            SupportsSqlFilterPushdown = false,
            SupportsEnforcedPolicyPushdown = false,
            SupportedSpatialPredicates =
            [
                SpatialRelationship.Intersects,
                SpatialRelationship.EnvelopeIntersects,
                SpatialRelationship.Within,
                SpatialRelationship.Contains,
                SpatialRelationship.Disjoint,
            ],
            CrsBehavior = new WarehouseCrsBehavior
            {
                SupportsCrossSridFilter = false,
                SupportsOutputReprojection = false,
                AssumesWgs84WhenUnset = true,
                AxisOrder = "xy",
                Notes = "Native GEOGRAPHY (WGS 84) and GEOMETRY types; cross-SRID filters are rejected up front.",
            },
            Aggregation = new WarehouseAggregationCapabilities
            {
                SupportsStatistics = true,
                SupportsGroupBy = true,
                SupportsHaving = true,
                SupportsH3Aggregation = true,
                SupportsH3Polyfill = true,
                SupportsH3KRing = true,
                RequiredExtensions = [],
            },
            RequiredExtensions = [],
        };

    private static WarehouseEffectiveSourceCapabilities BuildDatabricksCapabilities(string sourceId)
        => new()
        {
            ProviderName = DataProviderNames.Databricks,
            SourceId = sourceId,
            Availability = WarehouseSourceAvailability.Available,
            ReadOnly = true,
            SupportsBboxPushdown = true,
            SupportsTemporalFilter = false,
            SupportsSqlFilterPushdown = true,
            SupportsEnforcedPolicyPushdown = false,
            SupportedSpatialPredicates =
            [
                SpatialRelationship.Intersects,
                SpatialRelationship.EnvelopeIntersects,
                SpatialRelationship.Within,
                SpatialRelationship.Contains,
                SpatialRelationship.Disjoint,
                SpatialRelationship.Crosses,
                SpatialRelationship.Touches,
                SpatialRelationship.Overlaps,
                SpatialRelationship.Equals,
            ],
            CrsBehavior = new WarehouseCrsBehavior
            {
                SupportsCrossSridFilter = false,
                SupportsOutputReprojection = false,
                AssumesWgs84WhenUnset = true,
                AxisOrder = "xy",
                Notes = "Envelope and ISO WKB geometry predicates; cross-SRID filters are rejected up front.",
            },
            Aggregation = new WarehouseAggregationCapabilities
            {
                SupportsStatistics = true,
                SupportsGroupBy = true,
                SupportsHaving = false,
                SupportsH3Aggregation = true,
                SupportsH3Polyfill = true,
                SupportsH3KRing = true,
                RequiredExtensions = [],
            },
            RequiredExtensions = [],
        };

    private static WarehouseEffectiveSourceCapabilities BuildRedshiftCapabilities(string sourceId)
        => new()
        {
            ProviderName = DataProviderNames.Redshift,
            SourceId = sourceId,
            Availability = WarehouseSourceAvailability.Available,
            ReadOnly = true,
            SupportsBboxPushdown = true,
            SupportsTemporalFilter = false,
            SupportsSqlFilterPushdown = false,
            SupportsEnforcedPolicyPushdown = false,
            SupportedSpatialPredicates =
            [
                SpatialRelationship.Intersects,
                SpatialRelationship.EnvelopeIntersects,
                SpatialRelationship.Within,
                SpatialRelationship.Contains,
                SpatialRelationship.Disjoint,
            ],
            CrsBehavior = new WarehouseCrsBehavior
            {
                SupportsCrossSridFilter = false,
                SupportsOutputReprojection = false,
                AssumesWgs84WhenUnset = false,
                AxisOrder = "xy",
                Notes = "Requires matching filter and storage SRID; output SRID reprojection is not supported.",
            },
            Aggregation = new WarehouseAggregationCapabilities
            {
                SupportsStatistics = true,
                SupportsGroupBy = true,
                SupportsHaving = true,
                SupportsH3Aggregation = false,
                SupportsH3Polyfill = false,
                SupportsH3KRing = false,
                RequiredExtensions = [],
            },
            RequiredExtensions = [],
        };

    private static WarehouseEffectiveSourceCapabilities BuildUnsupportedCapabilities(
        string providerName,
        string sourceId)
        => new()
        {
            ProviderName = providerName,
            SourceId = sourceId,
            Availability = WarehouseSourceAvailability.Unsupported,
            AvailabilityReason = $"Provider '{providerName}' is not a supported warehouse analytics provider.",
            ReadOnly = true,
            SupportsBboxPushdown = false,
            SupportsTemporalFilter = false,
            SupportsSqlFilterPushdown = false,
            SupportsEnforcedPolicyPushdown = false,
            SupportedSpatialPredicates = [],
            CrsBehavior = new WarehouseCrsBehavior
            {
                SupportsCrossSridFilter = false,
                SupportsOutputReprojection = false,
                AssumesWgs84WhenUnset = false,
                AxisOrder = "xy",
                Notes = "Unsupported warehouse provider.",
            },
            Aggregation = new WarehouseAggregationCapabilities
            {
                SupportsStatistics = false,
                SupportsGroupBy = false,
                SupportsHaving = false,
                SupportsH3Aggregation = false,
                SupportsH3Polyfill = false,
                SupportsH3KRing = false,
                RequiredExtensions = [],
            },
            RequiredExtensions = [],
        };

    private static string NormalizeProvider(string? providerName)
        => string.IsNullOrWhiteSpace(providerName)
            ? string.Empty
            : DataProviderNames.Normalize(providerName);

    private static bool TryExtractScheme(string? uri, out string scheme)
    {
        scheme = string.Empty;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        var separatorIndex = uri.IndexOf("://", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        scheme = uri[..separatorIndex].Trim();
        return scheme.Length > 0;
    }
}
