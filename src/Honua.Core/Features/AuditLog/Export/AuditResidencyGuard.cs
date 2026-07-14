// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Export;

/// <summary>
/// Enforces audit data-residency policy: audit records may only be forwarded to
/// sinks whose declared <see cref="IAuditSink.Region"/> falls within the
/// configured set of allowed regions (#2157).
/// </summary>
/// <remarks>
/// <para>
/// Region comparison is case-insensitive. An empty allowed-region set means the
/// deployment places no residency restriction and every sink is permitted. When
/// the set is non-empty (restricted), a sink with a <c>null</c> region is
/// rejected because its destination cannot be proven to be in-region.
/// </para>
/// </remarks>
public sealed class AuditResidencyGuard
{
    private readonly HashSet<string> _allowedRegions;

    /// <summary>
    /// Initializes a new guard.
    /// </summary>
    /// <param name="allowedRegions">
    /// The allowed regions. Null/blank entries are ignored. An empty effective
    /// set means "unrestricted" — every sink is allowed.
    /// </param>
    public AuditResidencyGuard(IEnumerable<string>? allowedRegions)
    {
        _allowedRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedRegions is not null)
        {
            foreach (var region in allowedRegions.Where(region => !string.IsNullOrWhiteSpace(region)))
            {
                _allowedRegions.Add(region.Trim());
            }
        }
    }

    /// <summary>
    /// Whether a residency restriction is in effect (the allowed-region set is non-empty).
    /// </summary>
    public bool IsRestricted => _allowedRegions.Count > 0;

    /// <summary>
    /// Determines whether a sink with the given region may receive audit data.
    /// </summary>
    /// <param name="sinkRegion">The sink's declared region, or null.</param>
    /// <returns>
    /// <c>true</c> when unrestricted, or when restricted and <paramref name="sinkRegion"/>
    /// is a non-blank value contained in the allowed set; otherwise <c>false</c>.
    /// </returns>
    public bool IsAllowed(string? sinkRegion)
    {
        if (!IsRestricted)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(sinkRegion) && _allowedRegions.Contains(sinkRegion.Trim());
    }

    /// <summary>
    /// Throws <see cref="AuditResidencyViolationException"/> when the sink region
    /// is outside the allowed set.
    /// </summary>
    /// <param name="sinkRegion">The sink's declared region, or null.</param>
    /// <exception cref="AuditResidencyViolationException">When residency policy forbids the region.</exception>
    public void EnsureAllowed(string? sinkRegion)
    {
        if (!IsAllowed(sinkRegion))
        {
            throw new AuditResidencyViolationException(sinkRegion, _allowedRegions);
        }
    }
}

/// <summary>
/// Raised when an audit sink's region is outside the configured data-residency
/// allow-list. Surfaced by <see cref="AuditResidencyGuard.EnsureAllowed"/>.
/// </summary>
public sealed class AuditResidencyViolationException : Exception
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="sinkRegion">The rejected sink region, or null.</param>
    /// <param name="allowedRegions">The configured allowed regions.</param>
    public AuditResidencyViolationException(string? sinkRegion, IReadOnlyCollection<string> allowedRegions)
        : base(BuildMessage(sinkRegion, allowedRegions))
    {
        SinkRegion = sinkRegion;
        AllowedRegions = allowedRegions is null
            ? Array.Empty<string>()
            : allowedRegions.ToArray();
    }

    /// <summary>The rejected sink region, or null when the sink had no region.</summary>
    public string? SinkRegion { get; }

    /// <summary>The configured allowed regions at the time of the violation.</summary>
    public IReadOnlyList<string> AllowedRegions { get; }

    private static string BuildMessage(string? sinkRegion, IReadOnlyCollection<string>? allowedRegions)
    {
        var region = string.IsNullOrWhiteSpace(sinkRegion) ? "(none)" : sinkRegion;
        var allowed = allowedRegions is { Count: > 0 }
            ? string.Join(", ", allowedRegions)
            : "(none)";
        return $"Audit sink region '{region}' is outside the configured data-residency allow-list [{allowed}].";
    }
}
