// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal static class ExecutionJobMetadata
{
    public static string? GetParameter(ExecutionJobRecord job, string key)
        => job.Spec.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public static string? ResolveQueue(ExecutionJobRecord job)
        => GetParameter(job, ExecutionJobParameterKeys.Queue)
            ?? Normalize(job.Spec.Backend);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
