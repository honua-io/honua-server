// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// Endpoint metadata for a live stream whose access depends on the resource as well as the credential.
// The check context carries the re-authenticated principal, the stream's route values and a fresh service scope.
internal interface ILiveStreamResourceAuthorization
{
    Task<bool> IsAuthorizedAsync(HttpContext check, CancellationToken cancellationToken);
}
