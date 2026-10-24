// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Helpers;

internal static class FeatureAttributeVisibility
{
    internal static bool IsInternalAttribute(string? attributeName)
        => !string.IsNullOrWhiteSpace(attributeName) &&
           attributeName.StartsWith("__", StringComparison.Ordinal);
}
