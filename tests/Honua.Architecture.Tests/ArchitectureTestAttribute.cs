// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Marks a test as an architecture test (validates code structure and dependencies).
/// Architecture tests enforce design rules and prevent regressions.
/// </summary>
[TraitAttribute("Category", "Architecture")]
public sealed class ArchitectureTestAttribute : FactAttribute
{
}
