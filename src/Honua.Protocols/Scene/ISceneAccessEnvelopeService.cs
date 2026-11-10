// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.Scene.Models;

namespace Honua.Protocols.Scene;

internal interface ISceneAccessEnvelopeService
{
    SceneAccessEnvelope Issue(string sceneId);

    EnvelopeValidationResult Validate(string rawToken, string requestedSceneId);
}
