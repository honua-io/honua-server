// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

// One batch rule for every dead-letter redrive entry point (admin REST and ops actions).
internal static class AlertRedriveBatchLimit
{
    public const int Default = 500;

    public const int Maximum = 5000;

    public static int Resolve(int? requested)
        => requested is > 0 ? Math.Min(requested.Value, Maximum) : Default;
}
