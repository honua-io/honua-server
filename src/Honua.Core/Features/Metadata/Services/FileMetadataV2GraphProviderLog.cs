// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Metadata.Services;

internal static partial class FileMetadataV2GraphProviderLog
{
    [LoggerMessage(EventId = 22001, Level = LogLevel.Information,
        Message = "Loaded Metadata v2 graph from {Path}: revision={Revision} environment={Environment} resources={Resources} services={Services} publications={Publications}")]
    public static partial void GraphLoaded(
        ILogger logger,
        string path,
        long revision,
        string environment,
        int resources,
        int services,
        int publications);

    [LoggerMessage(EventId = 22002, Level = LogLevel.Error,
        Message = "Metadata v2 graph at {Path} failed validation: {Errors}")]
    public static partial void GraphValidationFailed(ILogger logger, string path, string errors);
}
