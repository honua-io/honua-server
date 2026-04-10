// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Helpers;

internal static class FeatureAttributeVisibility
{
    internal static bool IsInternalAttribute(string? attributeName)
        => !string.IsNullOrWhiteSpace(attributeName) &&
           attributeName.StartsWith("__", StringComparison.Ordinal);
}
