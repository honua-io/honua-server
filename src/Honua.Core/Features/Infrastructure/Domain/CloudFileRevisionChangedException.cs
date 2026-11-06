// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// The object generation changed between metadata resolution and conditional byte acquisition.
/// </summary>
public sealed class CloudFileRevisionChangedException : IOException
{
    /// <summary>Creates a provider-neutral revision mismatch without object paths or credentials.</summary>
    public CloudFileRevisionChangedException()
        : base("The artifact revision changed before its bytes could be read.")
    {
    }
}
