// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Federation.Connectors;

/// <summary>
/// Source-generated structured logging for the HTTP federated-source connectors (issue #341).
/// </summary>
internal static partial class FederationConnectorLog
{
    [LoggerMessage(EventId = 4520, Level = LogLevel.Warning,
        Message = "Failed to parse the GeoJSON response from a '{Kind}' federated source")]
    public static partial void ResponseParseFailed(ILogger logger, string kind, Exception exception);
}
