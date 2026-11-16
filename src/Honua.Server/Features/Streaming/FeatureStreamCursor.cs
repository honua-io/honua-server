// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Streaming;

// Feature-stream positions on the wire are features:<generation> (specification
// realtime-cross-node-rc3 section 9.1). A bare number is a position from before the change log
// was the source; it says nothing about generations, so the stream resets instead of guessing.
internal readonly record struct FeatureStreamCursor(FeatureStreamCursorKind Kind, long Generation)
{
    private static readonly string _prefix = RealtimeFamily.Features.Name + ":";

    public static FeatureStreamCursor None { get; } = new(FeatureStreamCursorKind.None, 0);

    public static string Format(long generation) => new RealtimePosition(RealtimeFamily.Features, generation).ToString();

    // The cursor query parameter wins over the Last-Event-ID header, as browsers send the header
    // on reconnect only.
    public static FeatureStreamCursor Parse(string? cursorParameter, string? lastEventId)
    {
        var raw = !string.IsNullOrWhiteSpace(cursorParameter) ? cursorParameter : lastEventId;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return None;
        }

        raw = raw.Trim();
        if (raw.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return long.TryParse(raw.AsSpan(_prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                ? new FeatureStreamCursor(FeatureStreamCursorKind.Position, generation)
                : new FeatureStreamCursor(FeatureStreamCursorKind.Invalid, 0);
        }

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? new FeatureStreamCursor(FeatureStreamCursorKind.Epoch, 0)
            : new FeatureStreamCursor(FeatureStreamCursorKind.Invalid, 0);
    }

    public long? Position => Kind == FeatureStreamCursorKind.Position ? Generation : null;
}

internal enum FeatureStreamCursorKind
{
    None,
    Position,
    Epoch,
    Invalid
}
