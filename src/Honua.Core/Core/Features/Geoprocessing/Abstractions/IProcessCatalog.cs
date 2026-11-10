// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Read-only catalog of available geoprocessing operations.
/// Implementations must be safe for concurrent access.
/// </summary>
public interface IProcessCatalog
{
    /// <summary>
    /// Returns the process definition for the given identifier, or <c>null</c> if not found.
    /// </summary>
    ProcessDefinition? GetProcess(string processId);

    /// <summary>
    /// Returns all registered process definitions.
    /// </summary>
    IReadOnlyList<ProcessDefinition> ListProcesses();

    /// <summary>
    /// Returns all process definitions belonging to the given category.
    /// </summary>
    IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category);
}
