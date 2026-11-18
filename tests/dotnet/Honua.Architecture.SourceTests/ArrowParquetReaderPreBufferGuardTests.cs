// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// A pre-buffered ParquetSharp Arrow reader over a managed Stream lets an Arrow IO thread drop the last
// native reference after Dispose. The native destructor then calls Close through a collected delegate
// and the process dies with an AccessViolationException, so every such reader turns pre-buffering off.
[Trait("Category", "Architecture")]
public sealed partial class ArrowParquetReaderPreBufferGuardTests
{
    [ArchitectureTest]
    public void EveryArrowParquetReader_TurnsPreBufferingOff()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var offenders = new[] { "src", ArchitectureTestHelpers.CombinePath("tests", "dotnet") }
            .SelectMany(root => Directory.EnumerateFiles(Path.Join(repoRoot, root), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .Where(file => ConstructsArrowReader(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .ToArray();

        offenders.Should().BeEmpty(
            "open ParquetSharp Arrow readers in tests through ArrowParquetTestReader.Open, or set PreBuffer = false next to the constructor");
    }

    private static bool ConstructsArrowReader(string source)
        => source.Contains("ParquetSharp", StringComparison.Ordinal)
            && ArrowReaderConstruction().IsMatch(source)
            && !PreBufferOff().IsMatch(source);

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex(@"\bnew\s+(ParquetSharp\.Arrow\.)?FileReader\s*\(")]
    private static partial Regex ArrowReaderConstruction();

    [GeneratedRegex(@"\bPreBuffer\s*=\s*false\b")]
    private static partial Regex PreBufferOff();
}
