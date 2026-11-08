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
    [InlineData("::Method()|src/Other/Store.cs::Method()", true)]
    [InlineData("\treturn task.Result;", true)]
    [InlineData("::Method()|src/Other/Store.cs::Method()", false)]
    public void ReadParentEntries_SourceChanged_MapsOnlyDetectedRenames(string entrySuffix, bool preserveSource)
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
            if (!preserveSource)
            {
                File.WriteAllText(Path.Join(root, movedPath), "public sealed record Replacement(string Value);");
            }
            File.WriteAllText(Path.Join(root, baselinePath), movedPath + entrySuffix + "\n");
            Commit(root);

            var prior = SourceTextRatchet.ReadParentEntries(root, baselinePath);

            prior.Should().Equal((preserveSource ? movedPath : originalPath) + entrySuffix);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnitTest]
    public void CanonicalPair_ReversedMembers_PreservesPairIdentity()
    {
        var forward = DuplicateMethodRatchetTests.CanonicalPair("src/Alpha.cs::Method()|src/Beta.cs::Method()");
        var reversed = DuplicateMethodRatchetTests.CanonicalPair("src/Beta.cs::Method()|src/Alpha.cs::Method()");

        reversed.Should().Be(forward);
    }

    [UnitTheory]
    [InlineData("src/Alpha.cs::Changed()|src/Beta.cs::Method()")]
    [InlineData("src/Gamma.cs::Method()|src/Beta.cs::Method()")]
    public void CanonicalPair_ChangedMember_HasDifferentIdentity(string changedPair)
    {
        var original = DuplicateMethodRatchetTests.CanonicalPair("src/Alpha.cs::Method()|src/Beta.cs::Method()");
        var changed = DuplicateMethodRatchetTests.CanonicalPair(changedPair);

        changed.Should().NotBe(original);
    }

    private static void Commit(string root)
    {
        SourceTextRatchet.RunGit(root, "add", ".");
        SourceTextRatchet.RunGit(root, "-c", "user.name=Mike McDougall", "-c", "user.email=mike@honua.io",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "source baseline");
    }
}
