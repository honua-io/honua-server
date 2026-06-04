// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// MapServer reuse shim. Thin, additive forwarder so the MapServer protocol
// surface can invoke the existing FeatureServer queryAttachments handler as-is
// (objectIds/returnUrl/filter parsing, layer access validation, storage-layer
// resolution, and the AttachmentHandler.QueryAttachmentsAsync call all live in
// the existing private handler).
//
// IMPORTANT FOR SIBLING BRANCHES: AttachmentEndpoints was made `partial` (a
// single-token change on its class declaration) so this new file can forward to
// the existing private HandleQueryAttachments without copying its logic. No
// existing attachment behavior changed. The MapServer route supplies the same
// {serviceId}/{layerId:int} route values the handler reads.

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
