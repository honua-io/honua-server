// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Security.Domain;

[SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "VerifyCA is a required compatibility alias for existing callers.")]
public enum SslMode
{
    Disable = 0,

    Allow = 1,

    Prefer = 2,

    Require = 3,

    VerifyCa = 4,

    VerifyCA = VerifyCa,

    VerifyFull = 5
}
