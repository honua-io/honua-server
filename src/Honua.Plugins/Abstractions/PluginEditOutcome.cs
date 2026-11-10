// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Plugins.Abstractions;

public sealed record PluginEditRejection(
    EditKind Kind,
    int RequestIndex,
    long? ObjectId,
    int ErrorCode,
    string Message);

public sealed record PluginEditOutcome(
    ImmutableArray<PluginEditRejection> Rejections,
    bool BatchRejected)
{
    public static PluginEditOutcome Allowed { get; } =
        new(ImmutableArray<PluginEditRejection>.Empty, BatchRejected: false);

    public bool HasRejections => !Rejections.IsDefaultOrEmpty;
}
