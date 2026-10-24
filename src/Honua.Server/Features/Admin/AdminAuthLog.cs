// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

/// <summary>
/// Source-generated logger for Admin auth features (AOT compatible).
/// </summary>
internal static partial class AdminAuthLog
{
    /// <summary>
    /// Logs when the auth config endpoint serves provider metadata.
    /// </summary>
    [LoggerMessage(
        EventId = 4006,
        Level = LogLevel.Debug,
        Message = "Admin auth config served with {ProviderCount} provider(s)")]
    public static partial void AuthConfigServed(ILogger logger, int providerCount);
}
