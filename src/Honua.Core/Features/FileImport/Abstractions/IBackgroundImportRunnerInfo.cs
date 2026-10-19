// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FileImport.Abstractions;

/// <summary>
/// The background-import runner this host resolved once at composition (honua-server#5916).
/// When absent, a registered <see cref="IImportJobService"/> is assumed to accept background imports.
/// </summary>
public interface IBackgroundImportRunnerInfo
{
    /// <summary>
    /// <c>batch</c>, <c>in-process</c> or <c>unavailable</c>
    /// (<see cref="Domain.ImportJobRunners"/>).
    /// </summary>
    string Runner { get; }

    /// <summary>Whether this host accepts background imports.</summary>
    bool AcceptsBackgroundImports { get; }

    /// <summary>Why background imports are unavailable; null when they are available.</summary>
    string? UnavailableReason { get; }
}
