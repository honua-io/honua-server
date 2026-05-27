// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console;

/// <summary>
/// Structured logging hooks for the Console Share access endpoints. Failure-path
/// resolution events deliberately omit item identifiers so the anonymous public
/// surface cannot be turned into a log-driven enumeration oracle.
/// </summary>
internal static partial class ConsoleShareEndpointsLog
{
    [LoggerMessage(EventId = 4820, Level = LogLevel.Information,
        Message = "Console share access tier changed for {ItemId}: {OldTier} -> {NewTier} by {PrincipalId}")]
    public static partial void ShareAccessTierChanged(ILogger logger, string itemId, ConsoleShareAccessTier oldTier, ConsoleShareAccessTier newTier, string? principalId);

    [LoggerMessage(EventId = 4821, Level = LogLevel.Warning,
        Message = "Console share closure validation bypassed for {ItemId} -> {TargetTier} by {PrincipalId} ({ConflictCount} conflicts overridden)")]
    public static partial void DependencyClosureBypassed(ILogger logger, string itemId, ConsoleShareAccessTier targetTier, string? principalId, int conflictCount);

    [LoggerMessage(EventId = 4822, Level = LogLevel.Information,
        Message = "Console share closure conflict for {ItemId} -> {TargetTier} ({ConflictCount} conflicts)")]
    public static partial void DependencyClosureConflict(ILogger logger, string itemId, ConsoleShareAccessTier targetTier, int conflictCount);

    [LoggerMessage(EventId = 4823, Level = LogLevel.Debug,
        Message = "Console share closure walk hit depth cap {Cap} for {ItemId}")]
    public static partial void DependencyClosureDepthCapped(ILogger logger, string itemId, int cap);

    [LoggerMessage(EventId = 4824, Level = LogLevel.Information,
        Message = "Console public-link minted for {ItemId}: token {TokenId} by {PrincipalId} (expires {ExpiresAt})")]
    public static partial void PublicLinkMinted(ILogger logger, string itemId, string tokenId, string? principalId, DateTimeOffset? expiresAt);

    [LoggerMessage(EventId = 4825, Level = LogLevel.Information,
        Message = "Console public-link revoked for {ItemId}: token {TokenId} by {PrincipalId}")]
    public static partial void PublicLinkExpired(ILogger logger, string itemId, string tokenId, string? principalId);

    [LoggerMessage(EventId = 4826, Level = LogLevel.Information,
        Message = "Console public-link resolved: token {TokenId} for {ItemId}")]
    public static partial void PublicLinkResolveGranted(ILogger logger, string tokenId, string itemId);

    [LoggerMessage(EventId = 4827, Level = LogLevel.Information,
        Message = "Console public-link resolution denied")]
    public static partial void PublicLinkResolveDenied(ILogger logger);

    [LoggerMessage(EventId = 4832, Level = LogLevel.Information,
        Message = "Console public content resolved for {ItemId}")]
    public static partial void PublicContentResolveGranted(ILogger logger, string itemId);

    [LoggerMessage(EventId = 4833, Level = LogLevel.Information,
        Message = "Console public content resolution denied")]
    public static partial void PublicContentResolveDenied(ILogger logger);

    [LoggerMessage(EventId = 4828, Level = LogLevel.Information,
        Message = "Console embed {Enabled} for {ItemId} (audience {Audience}) by {PrincipalId}")]
    public static partial void EmbedConfigured(ILogger logger, string itemId, bool enabled, ConsoleEmbedAudience? audience, string? principalId);

    [LoggerMessage(EventId = 4829, Level = LogLevel.Information,
        Message = "Console embed token minted for {ItemId}: token {TokenId} (audience {Audience}) by {PrincipalId}")]
    public static partial void EmbedTokenMinted(ILogger logger, string itemId, string tokenId, ConsoleEmbedAudience audience, string? principalId);

    [LoggerMessage(EventId = 4830, Level = LogLevel.Information,
        Message = "Console embed token redeemed: token {TokenId} for {ItemId} (audience {Audience})")]
    public static partial void EmbedRedeemGranted(ILogger logger, string tokenId, string itemId, ConsoleEmbedAudience audience);

    [LoggerMessage(EventId = 4831, Level = LogLevel.Information,
        Message = "Console embed token redemption denied")]
    public static partial void EmbedRedeemDenied(ILogger logger);
}
