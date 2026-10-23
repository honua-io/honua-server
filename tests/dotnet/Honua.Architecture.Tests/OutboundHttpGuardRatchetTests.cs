// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ratchet for local outbound HTTP guards under <c>src/</c>.
/// </summary>
/// <remarks>
/// New pinned-DNS and private-address checks belong on
/// <c>OutboundHttpUrlValidator</c> and <c>MigrationPinnedDns</c>. A file that
/// declares its own <c>IsPrivateOrReservedAddress</c> or connect-pin method, or
/// that opens a <c>Socket</c> from a <c>ConnectCallback</c> without delegating
/// to <c>MigrationPinnedDns</c>, is a local guard. Callers that only forward to
/// <c>MigrationPinnedDns</c> are not. The baseline lists the copies that already
/// exist and may only shrink once it is on trunk.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed partial class OutboundHttpGuardRatchetTests
{
    private const string RelativeBaseline = "tests/dotnet/Honua.Architecture.Tests/outbound-http-guard-baseline.txt";

    private static readonly string[] CanonicalImplementations =
    [
        "src/Honua.Core/Features/Infrastructure/Validation/OutboundHttpUrlValidator.cs",
        "src/Honua.Core/Features/Migration/Services/MigrationPinnedDns.cs",
    ];

    [ArchitectureTest]
    public void LocalOutboundGuards_MustMatchTheBaseline()
    {
        var actual = Scan();
        var baseline = SourceTextRatchet.ReadEntries(BaselinePath());
        var added = actual.Except(baseline, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var stale = baseline.Except(actual, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();

        string.Join('\n', added).Should().BeEmpty();
        string.Join('\n', stale).Should().BeEmpty();
    }

    [ArchitectureTest]
    public void OutboundHttpGuardBaseline_MustNotGrowFromTheCheckedOutBase()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var current = SourceTextRatchet.ReadEntries(BaselinePath());
        var prior = SourceTextRatchet.ReadParentEntries(root, RelativeBaseline);
        if (prior is null)
        {
            return;
        }

        var added = current.Except(prior, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        string.Join('\n', added).Should().BeEmpty();
    }

    private static string BaselinePath()
        => SourceTextRatchet.AbsolutePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), RelativeBaseline);

    internal static string[] Scan()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var src = ArchitectureTestHelpers.CombinePath(root, "src");
        var canonical = new HashSet<string>(CanonicalImplementations, StringComparer.Ordinal);
        var guards = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal) ||
                canonical.Contains(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var declared = false;
            foreach (var line in text.Split('\n'))
            {
                if (LocalGuardDeclaration().IsMatch(line))
                {
                    declared = true;
                    break;
                }
            }

            // "new Socket(" so "new SocketsHttpHandler" is not a pinned socket.
            var inlinePin = text.Contains("ConnectCallback", StringComparison.Ordinal) &&
                text.Contains("new Socket(", StringComparison.Ordinal) &&
                !text.Contains("MigrationPinnedDns.", StringComparison.Ordinal);
            if (declared || inlinePin)
            {
                guards.Add(relative);
            }
        }

        guards.Sort(StringComparer.Ordinal);
        return guards.ToArray();
    }

    [GeneratedRegex(
        """^\s*(?:(?:public|private|protected|internal|static|async|sealed|override|virtual|unsafe|new|partial|extern|required|file)\s+)+(?:[\w.<>,\[\]?]+\s+)+(?:IsPrivateOrReservedAddressCore|IsPrivateOrReservedAddress|ConnectWithPinnedDnsAsync|ConnectPinnedAsync|ConnectWithValidatedAddressAsync)\s*\(""",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocalGuardDeclaration();
}
