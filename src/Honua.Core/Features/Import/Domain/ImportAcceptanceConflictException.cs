// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// An existing durable import identity is bound to different immutable inputs.
/// Canonical import adapters report this as a conflict, without retrying or replacing the job.
/// </summary>
public sealed class ImportAcceptanceConflictException : InvalidOperationException
{
    /// <summary>Creates a conflict with a safe, non-resource-disclosing message.</summary>
    public ImportAcceptanceConflictException()
        : base("The import acceptance identity is already bound to a different immutable request.")
    {
    }

    /// <summary>Creates a conflict with the specified message.</summary>
    public ImportAcceptanceConflictException(string message) : base(message)
    {
    }

    /// <summary>Creates a conflict with the specified message and cause.</summary>
    public ImportAcceptanceConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
