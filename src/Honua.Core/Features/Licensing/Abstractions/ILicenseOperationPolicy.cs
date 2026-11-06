// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Abstractions;

public interface ILicenseOperationPolicy
{
    bool IsBlocked { get; }

    // Capture the cancellation token once per operation; a renewal does not revive an operation cancelled by an earlier expiry.
    CancellationToken OperationCancellation { get; }
}
