// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class SourceTextRatchetTests
{
    [UnitTheory]
    [InlineData("::Method()|src/Other/Store.cs::Method()")]
    [InlineData("\treturn task.Result;")]
    public void ReadParentEntries_SourceMoved_PreservesDebtIdentity(string entrySuffix)
    {
        var root = Directory.CreateTempSubdirectory("honua-source-ratchet-").FullName;
        try
        {
            const string originalPath = "src/Store/Store.cs";
            const string movedPath = "src/Db/Store/Store.cs";
            const string baselinePath = "baseline.txt";
            SourceTextRatchet.RunGit(root, "init", "--quiet");
            Directory.CreateDirectory(Path.Join(root, "src", "Store"));
            File.WriteAllText(Path.Join(root, originalPath), "internal class Store { }");
            File.WriteAllText(Path.Join(root, baselinePath), originalPath + entrySuffix + "\n");
            Commit(root);

            Directory.CreateDirectory(Path.Join(root, "src", "Db", "Store"));
            File.Move(Path.Join(root, originalPath), Path.Join(root, movedPath));
            File.WriteAllText(Path.Join(root, baselinePath), movedPath + entrySuffix + "\n");
            Commit(root);

            var prior = SourceTextRatchet.ReadParentEntries(root, baselinePath);

            prior.Should().Equal(movedPath + entrySuffix);
            SourceTextRatchet.ReadEntries(Path.Join(root, baselinePath))
                .Except(prior!, StringComparer.Ordinal).Should().BeEmpty();
            new[] { movedPath + "::NewMethod()" }
                .Except(prior!, StringComparer.Ordinal).Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Commit(string root)
    {
        SourceTextRatchet.RunGit(root, "add", ".");
        SourceTextRatchet.RunGit(root, "-c", "user.name=Mike McDougall", "-c", "user.email=mike@honua.io",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "source baseline");
    }
}
