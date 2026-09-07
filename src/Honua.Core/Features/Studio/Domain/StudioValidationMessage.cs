// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Domain;

/// <summary>
/// Canonical rendering of a Studio validation rejection into the single-line message the REST and
/// MCP surfaces hand back to callers. The lifecycle service and the durable operation executors
/// both reject on the same diagnostics, so they must produce byte-identical detail regardless of
/// which one runs first.
/// </summary>
public static class StudioValidationMessage
{
    /// <summary>
    /// Renders <paramref name="prefix"/> followed by every diagnostic message, for example
    /// <c>"Publication intent is invalid: route must start with '/'."</c>.
    /// </summary>
    public static string Format(string prefix, IReadOnlyList<StudioValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(diagnostics);

        return diagnostics.Count == 0
            ? prefix + "."
            : prefix + ": " + string.Join("; ", diagnostics.Select(static diagnostic => diagnostic.Message));
    }
}
