// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Abstractions;

/// <summary>Controls all data operations on a paid deployment, including existing-data reads.</summary>
public interface ILicenseOperationPolicy
{
    /// <summary>Whether the deployment's license currently prevents data operations.</summary>
    bool IsBlocked { get; }

    /// <summary>
    /// Cancellation for the current valid-license period. Capture once per operation;
    /// renewal never revives an operation cancelled by an earlier expiry.
    /// </summary>
    CancellationToken OperationCancellation { get; }
}
