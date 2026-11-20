// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

// Seals a stored submitter snapshot to the record that carries it (the binding), with a key the
// store's writers do not hold, so an edited or copied snapshot does not verify.
public interface IJobSecurityContextSealer
{
    string Seal(string binding, JobSecurityContext context);

    bool Verify(string binding, JobSecurityContext context, string? seal);
}
