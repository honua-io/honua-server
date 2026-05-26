// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Share.Domain;

namespace Honua.Core.Features.Share.Abstractions;

/// <summary>
/// Resolves whether a Share export destination family can be executed in the current build and
/// environment. The resolver is the single decision point that distinguishes a destination with a
/// registered worker (<see cref="ShareExportDestinationStatus.Supported"/>) from one that has no
/// worker in this build (<see cref="ShareExportDestinationStatus.Unsupported"/>) or one that is
/// known but lacks credentials/configuration (<see cref="ShareExportDestinationStatus.NotConfigured"/>).
/// </summary>
public interface IShareExportDestinationResolver
{
    /// <summary>
    /// Resolves the availability of a destination family.
    /// </summary>
    /// <param name="destinationType">Destination family to resolve.</param>
    /// <returns>The resolved availability status.</returns>
    ShareExportDestinationStatus Resolve(ShareExportDestinationType destinationType);
}
