// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.CustomCode.Harness;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class SourceFetchTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ClonePinned_NonSha_RefusedBeforeGit()
    {
        var called = false;
        var fetch = new SourceFetch((_, _) => { called = true; return new ProcessResult(0, "", ""); });

        var act = () => fetch.ClonePinned("https://github.com/x/y.git", "main", "/tmp/ccnet-x");

        act.Should().Throw<SourceFetchException>().WithMessage("*must be 40-hex*");
        called.Should().BeFalse("git must never be invoked with an unvalidated ref");
    }

    [Fact]
    public void ClonePinned_VerifiesHeadEqualsSha()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-fetch");
        try
        {
            // Fake git: every command succeeds; rev-parse returns the requested SHA.
            ProcessResult Runner(IReadOnlyList<string> args, string? cwd)
                => args is ["rev-parse", "HEAD"]
                    ? new ProcessResult(0, ValidSha + "\n", "")
                    : new ProcessResult(0, "", "");

            var fetch = new SourceFetch(Runner);
            var result = fetch.ClonePinned("https://github.com/x/y.git", ValidSha, dir.FullName);

            result.Should().Be(dir.FullName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ClonePinned_HeadMismatch_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-fetch");
        try
        {
            ProcessResult Runner(IReadOnlyList<string> args, string? cwd)
                => args is ["rev-parse", "HEAD"]
                    ? new ProcessResult(0, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n", "")
                    : new ProcessResult(0, "", "");

            var fetch = new SourceFetch(Runner);
            var act = () => fetch.ClonePinned("https://github.com/x/y.git", ValidSha, dir.FullName);

            act.Should().Throw<SourceFetchException>().WithMessage("*checkout verification failed*");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ClonePinned_FallsBackWhenDirectShaFetchFails()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-fetch");
        try
        {
            var fetchedByShaDirectly = false;
            var fellBack = false;

            ProcessResult Runner(IReadOnlyList<string> args, string? cwd)
            {
                if (args.Count >= 5 && args[0] == "fetch" && args[^1] == ValidSha)
                {
                    fetchedByShaDirectly = true;
                    return new ProcessResult(128, "", "server does not allow request for unadvertised object");
                }

                if (args is ["fetch", "--depth", "50", "origin"])
                {
                    fellBack = true;
                }

                return args is ["rev-parse", "HEAD"]
                    ? new ProcessResult(0, ValidSha, "")
                    : new ProcessResult(0, "", "");
            }

            var fetch = new SourceFetch(Runner);
            fetch.ClonePinned("https://github.com/x/y.git", ValidSha, dir.FullName);

            fetchedByShaDirectly.Should().BeTrue();
            fellBack.Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
