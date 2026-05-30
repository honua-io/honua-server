// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Scene.Models;

namespace Honua.Protocols.Scene;

/// <summary>
/// Issues and verifies short-lived signed envelopes that authorize CesiumJS
/// nested 3D Tiles asset cascades on protected scenes.
/// </summary>
internal interface ISceneAccessEnvelopeService
{
    /// <summary>
    /// Issues a fresh envelope for <paramref name="sceneId"/>. Callers are
    /// responsible for ensuring the principal is authorized for the scene
    /// before invoking this method.
    /// </summary>
    SceneAccessEnvelope Issue(string sceneId);

    /// <summary>
    /// Validates a wire token and confirms it is bound to the requested
    /// scene. Returns the granular failure mode so the endpoint can map
    /// to the correct HTTP status without revealing payload contents on
    /// signature failures.
    /// </summary>
    EnvelopeValidationResult Validate(string rawToken, string requestedSceneId);
}
