// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

public static class OperationDescriptorPublication
{
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

    public static IReadOnlyDictionary<string, int> CountMappings(
        IEnumerable<IOperationApprovalRequestMapper> mappers)
        => mappers
            .GroupBy(mapper => mapper.OperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
