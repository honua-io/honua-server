// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Studio.Export;

/// <summary>
/// Thrown by <see cref="StudioDeliverableComposer"/> when the resolved rendering typeface cannot
/// draw glyphs, so composing would otherwise silently succeed with a blank artifact
/// (honua-server#4908).
/// </summary>
internal sealed class StudioDeliverableRenderException : Exception
{
    public StudioDeliverableRenderException(string code, string detail)
        : base(detail)
    {
        Code = code;
    }

    /// <summary>Machine-readable reason code surfaced on the export result and HTTP problem response.</summary>
    public string Code { get; }
}
