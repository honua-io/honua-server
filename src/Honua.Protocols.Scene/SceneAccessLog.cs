// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Scene;

/// <summary>
/// Source-generated logging events for scene access envelope issuance and
/// validation. EventId range 8410–8419. No log entry contains the raw token,
/// HMAC value, signing key, storage credential, or absolute filesystem path.
/// The <see cref="OptionsMisconfigured"/> reason is constructed from
/// well-known option keys (e.g. <c>Honua:SceneAccessSigning:SigningKey</c>)
/// and bounded constants — never from caller-supplied or sensitive values —
/// so it is safe to log verbatim.
/// </summary>
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
