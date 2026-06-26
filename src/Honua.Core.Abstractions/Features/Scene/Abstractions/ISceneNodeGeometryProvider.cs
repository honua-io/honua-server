// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

/// <summary>
/// Serving seam that produces the transcoded I3S binary geometry for a single
/// node of a hosted scene (#1810). The Esri I3S SceneServer geometry route
/// (<c>nodes/{nodeId}/geometries/0</c>) adapts to this seam so the transcode
/// source (feature layer, baked staging, etc.) can vary without the protocol
/// endpoint reaching into a concrete pipeline.
/// </summary>
/// <remarks>
/// Implementations transcode the scene's renderable geometry with the I3S
/// geometry transcoder. A scene that carries no transcodable
/// geometry for the requested node (for example a hosted-tiles-only scene whose
/// glTF→I3S re-transcode is not yet wired) returns <see langword="null"/> so the
/// endpoint can answer a deterministic 404 rather than fabricating an empty
/// buffer.
/// </remarks>
public interface ISceneNodeGeometryProvider
{
    /// <summary>
    /// Produces the transcoded I3S geometry for the supplied scene node, or
    /// <see langword="null"/> when the scene/node has no servable geometry.
    /// </summary>
    /// <param name="scene">The hosted scene whose node geometry is requested.</param>
    /// <param name="nodeId">The I3S node id (the root whole-scene node is <c>0</c> in this slice).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transcoded geometry, or <see langword="null"/> when none is available.</returns>
    Task<I3sTranscodedGeometry?> GetNodeGeometryAsync(
        SceneDataset scene,
        int nodeId,
        CancellationToken cancellationToken = default);
}
