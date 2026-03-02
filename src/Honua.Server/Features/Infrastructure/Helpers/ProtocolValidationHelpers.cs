// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Helpers for validating that a protocol is enabled for a service.
/// </summary>
internal static class ProtocolValidationHelpers
{
    /// <summary>
    /// Returns a NotFound result if the given protocol is not enabled for the service.
    /// Returns null if the protocol is enabled.
    /// </summary>
    internal static IResult? ValidateProtocolEnabled(HttpContext context, ServiceDefinition service, string protocol)
    {
        if (!ServiceProtocols.IsProtocolEnabled(service.Metadata, protocol))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"{protocol} is not enabled for this service.");
        }

        return null;
    }

    /// <summary>
    /// Returns a NotFound result if the given protocol is not enabled for the supplied metadata.
    /// Returns null if the protocol is enabled.
    /// </summary>
    internal static IResult? ValidateProtocolEnabled(HttpContext context, CatalogMetadata? metadata, string protocol)
    {
        if (!ServiceProtocols.IsProtocolEnabled(metadata, protocol))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"{protocol} is not enabled for this service.");
        }

        return null;
    }
}
