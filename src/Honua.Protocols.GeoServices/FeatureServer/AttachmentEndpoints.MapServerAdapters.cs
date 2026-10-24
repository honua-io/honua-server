// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
//
// MapServer reuse shim. Forwards queryAttachments to the FeatureServer handler
// so object id, URL, and filter parsing stay in one place. The MapServer route
// supplies the same {serviceId}/{layerId:int} route values that handler reads.

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class AttachmentEndpoints
{
    /// <summary>
    /// MapServer adapter entry point that reuses the FeatureServer
    /// queryAttachments handler verbatim.
    /// </summary>
    internal static Task HandleQueryAttachmentsForMapServer(HttpContext context)
        => HandleQueryAttachments(context);
}
