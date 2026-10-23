// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Import.UnitTests.Import;

public sealed class LocalImportStorageRootsTests
{
    [UnitTest]
    public void ReopenedRootsRetainTheirBindingAndRejectAnotherJournalOwner()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var roots = disk.Open();
        roots.Provision();
        var fingerprint = roots.Fingerprint();
        disk.Open().Provision();
        disk.Open().RequireFingerprint(fingerprint);
        Assert.Throws<InvalidDataException>(() => disk.Open("another-domain").Provision());
        disk.Open().RequireFingerprint(fingerprint);
    }

    [UnitTest]
    public void InterruptedMarkerCreationCannotBeSilentlyReplacedDuringBootstrap()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var marker = Path.Join(disk.Roots[ImportStorageRootKind.ProviderObjects], LocalImportStorageRoots.MarkerName);
        File.WriteAllText(marker, "partial");
        Assert.Throws<InvalidDataException>(() => disk.Open().Provision());
        Assert.Equal("partial", File.ReadAllText(marker));
    }

    [UnitTest]
    public void RootKindsCannotExchangeMarkers()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var roots = disk.Open();
        roots.Provision();
        var fingerprint = roots.Fingerprint();
        File.Copy(Path.Join(disk.Roots[ImportStorageRootKind.ProviderObjects], LocalImportStorageRoots.MarkerName),
            Path.Join(disk.Roots[ImportStorageRootKind.SourceScratch], LocalImportStorageRoots.MarkerName), overwrite: true);
        Assert.Throws<InvalidDataException>(() => roots.RequireFingerprint(fingerprint));
    }

    [UnitTest]
    public void SymlinkToOriginalRootDoesNotSatisfyPhysicalBinding()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var roots = disk.Open();
        roots.Provision();
        var fingerprint = roots.Fingerprint();
        var original = disk.Roots[ImportStorageRootKind.ProviderObjects];
        var moved = original + "-moved";
        Directory.Move(original, moved);
        Directory.CreateSymbolicLink(original, moved);
        Assert.Throws<IOException>(() => roots.RequireFingerprint(fingerprint));
    }

    private sealed class Disk : IDisposable
    {
        private readonly string _directory = Path.Join(Path.GetTempPath(), "honua-root-binding-" + Guid.NewGuid().ToString("N"));
        internal Dictionary<ImportStorageRootKind, string> Roots { get; } = [];

        internal Disk()
        {
            foreach (var kind in Enum.GetValues<ImportStorageRootKind>())
            {
                var path = Path.Join(_directory, kind.ToString());
                Directory.CreateDirectory(path);
                Roots.Add(kind, path);
            }
        }

        internal LocalImportStorageRoots Open(string domainId = "domain") => new(Roots, Path.Join(_directory, "journal"), domainId);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
