// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Scene;

internal static partial class SceneAccessLog
{
    [LoggerMessage(EventId = 8410, Level = LogLevel.Information,
        Message = "Issued scene access envelope for scene {SceneId}; expires at {ExpiresAtUtc:O}")]
    public static partial void EnvelopeIssued(ILogger logger, string sceneId, DateTimeOffset expiresAtUtc);

    [LoggerMessage(EventId = 8411, Level = LogLevel.Warning,
        Message = "Rejected scene access envelope for scene {SceneId}: token expired")]
    public static partial void TokenExpired(ILogger logger, string sceneId);

    [LoggerMessage(EventId = 8412, Level = LogLevel.Warning,
        Message = "Rejected scene access envelope for scene {SceneId}: token tampered or undecodable")]
    public static partial void TokenTampered(ILogger logger, string sceneId);

    [LoggerMessage(EventId = 8413, Level = LogLevel.Warning,
        Message = "Rejected scene access envelope for scene {SceneId}: token bound to different scene")]
    public static partial void TokenWrongScene(ILogger logger, string sceneId);

    [LoggerMessage(EventId = 8414, Level = LogLevel.Warning,
        Message = "Rejected scene access for scene {SceneId}: protected scene requires bearer auth or access envelope token")]
    public static partial void TokenMissing(ILogger logger, string sceneId);

    [LoggerMessage(EventId = 8415, Level = LogLevel.Error,
        Message = "Scene access envelope service is misconfigured for scene {SceneId}: {Reason}")]
    public static partial void OptionsMisconfigured(ILogger logger, string sceneId, string reason);
}
