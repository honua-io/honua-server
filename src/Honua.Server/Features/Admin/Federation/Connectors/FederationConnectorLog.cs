// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Federation.Connectors;

internal static partial class FederationConnectorLog
{
    [LoggerMessage(EventId = 4520, Level = LogLevel.Warning,
        Message = "Failed to parse the GeoJSON response from a '{Kind}' federated source")]
    public static partial void ResponseParseFailed(ILogger logger, string kind, Exception exception);
}
