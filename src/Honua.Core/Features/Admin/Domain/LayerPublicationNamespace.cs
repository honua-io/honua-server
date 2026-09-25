// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

/// <summary>Validation for an optional publication grouping namespace; this is not an authorization grant.</summary>
public static class LayerPublicationNamespace
{
    /// <summary>Accepts the legacy null namespace or a bounded, case-preserving metadata identifier.</summary>
    public static bool IsValid(string? value) => value is null ||
        (value.Length is >= 1 and <= 128 && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));
}
