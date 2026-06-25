// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// Whether a golden GP test ASSERTS against the recorded golden or REGENERATES it
/// (GP Devkit P6, issue #2127). Update mode is the deliberate, guarded escape hatch for
/// (re)authoring goldens after an intended behavior change; it is off by default and is
/// turned on by the <c>HONUA_GP_UPDATE_GOLDENS</c> environment variable (see
/// <see cref="GpGoldenAssert"/>) so a normal test run can never silently overwrite a golden.
/// </summary>
public enum GoldenUpdateMode
{
    /// <summary>Read the golden and assert the artifact matches it within tolerance (default).</summary>
    Assert = 0,

    /// <summary>Write the produced artifact to the golden path, (re)generating it.</summary>
    Update = 1,
}
