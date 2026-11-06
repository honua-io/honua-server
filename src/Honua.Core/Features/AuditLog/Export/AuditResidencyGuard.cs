// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Export;

// When the allow list is non-empty, a sink with no declared region is rejected because its destination cannot be shown to be in bounds.
public sealed class AuditResidencyGuard
{
    private readonly HashSet<string> _allowedRegions;

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

    public bool IsRestricted => _allowedRegions.Count > 0;

    public bool IsAllowed(string? sinkRegion)
    {
        if (!IsRestricted)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(sinkRegion) && _allowedRegions.Contains(sinkRegion.Trim());
    }

    public void EnsureAllowed(string? sinkRegion)
    {
        if (!IsAllowed(sinkRegion))
        {
            throw new AuditResidencyViolationException(sinkRegion, _allowedRegions);
        }
    }
}

public sealed class AuditResidencyViolationException : Exception
{
    public AuditResidencyViolationException(string? sinkRegion, IReadOnlyCollection<string> allowedRegions)
        : base(BuildMessage(sinkRegion, allowedRegions))
    {
        SinkRegion = sinkRegion;
        AllowedRegions = allowedRegions is null
            ? Array.Empty<string>()
            : allowedRegions.ToArray();
    }

    public string? SinkRegion { get; }

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
