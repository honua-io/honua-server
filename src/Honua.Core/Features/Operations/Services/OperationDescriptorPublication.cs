// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>Shared fail-closed predicate for public operation discovery and startup validation.</summary>
public static class OperationDescriptorPublication
{
    /// <summary>
    /// Returns true only when a descriptor is public and, if approval-capable, has one
    /// registered safe typed request mapping.
    /// </summary>
    public static bool CanAdvertise(
        IOperationDescriptor descriptor,
        IReadOnlyDictionary<string, int> mapperCounts)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(mapperCounts);

        return !descriptor.IsCompatibilityOnly &&
            (descriptor.ApprovalModel == OperationApprovalModel.None ||
             mapperCounts.TryGetValue(descriptor.OperationId, out var count) && count == 1);
    }

    /// <summary>Builds mapper counts used by both startup validation and public discovery.</summary>
    public static IReadOnlyDictionary<string, int> CountMappings(
        IEnumerable<IOperationApprovalRequestMapper> mappers)
        => mappers
            .GroupBy(mapper => mapper.OperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
