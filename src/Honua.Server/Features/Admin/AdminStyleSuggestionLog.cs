// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Source-generated log messages for style suggestion operations.
/// </summary>
internal static partial class AdminStyleSuggestionLog
{
    [LoggerMessage(
        EventId = 4570,
        Level = LogLevel.Information,
        Message = "Style suggestion requested for layer {LayerId}")]
    public static partial void StyleSuggestionRequested(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 4571,
        Level = LogLevel.Information,
        Message = "Style suggestion completed for layer {LayerId}: field={FieldName}, method={Method}, palette={Palette}")]
    public static partial void StyleSuggestionCompleted(
        ILogger logger, int layerId, string? fieldName, string? method, string? palette);

    [LoggerMessage(
        EventId = 4572,
        Level = LogLevel.Warning,
        Message = "Style suggestion failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void StyleSuggestionFailed(ILogger logger, int layerId, string errorMessage, Exception? exception);

    [LoggerMessage(
        EventId = 4573,
        Level = LogLevel.Information,
        Message = "Style suggestion returned geometry-only defaults for layer {LayerId} (edition={Edition})")]
    public static partial void StyleSuggestionGeometryOnly(ILogger logger, int layerId, string edition);
}
