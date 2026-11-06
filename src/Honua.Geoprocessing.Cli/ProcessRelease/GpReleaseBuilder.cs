// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.Cli.Release;

internal sealed record GpReleaseRequest
{
    public required string DescriptorPath { get; init; }

    public string? SourceRepository { get; init; }

    public string? SourceSha { get; init; }

    public string? RuntimeImage { get; init; }

    public string? GeneratedAt { get; init; }

    public required string OutputDirectory { get; init; }
}

internal sealed record GpReleaseResult
{
    public required GpProcessReleaseLock Lock { get; init; }

    public required string LockPath { get; init; }

    public required IReadOnlyList<string> ArtifactPaths { get; init; }
}

internal static class GpReleaseBuilder
{
    public const string LockFileName = "process-release-lock.json";

    public const string ArtifactsDirectoryName = "artifacts";

    private static readonly string[] ExcludedDirectoryNames =
        [".git", ".venv", "venv", "__pycache__", ".pytest_cache", "node_modules", "bin", "obj", ".vs", ".idea", "artifacts", ".honua-gp"];

    public static GpReleaseResult Build(GpReleaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptorPath = Path.GetFullPath(request.DescriptorPath);
        if (!File.Exists(descriptorPath))
        {
            throw new GpReleaseException([$"descriptor not found: {descriptorPath}"]);
        }

        var projectDirectory = Path.GetDirectoryName(descriptorPath)!;
        var pathErrors = new List<string>();
        if (!ValidateInputPath(projectDirectory, descriptorPath, "descriptor", pathErrors))
        {
            throw new GpReleaseException(pathErrors);
        }

        var descriptor = GpProcessDescriptor.Parse(File.ReadAllText(descriptorPath));
        var errors = new List<string>(descriptor.Validate());
        if (errors.Count > 0)
        {
            throw new GpReleaseException(errors);
        }

        var process = descriptor.Process!;
        var contract = process.Contract!;
        var git = GitFacts.Resolve(projectDirectory);
        if (git is null)
        {
            throw new GpReleaseException(["the descriptor is not inside a Git checkout; a release is built from a commit, never from loose files"]);
        }

        var sourceSha = (request.SourceSha ?? git.HeadSha).Trim().ToLowerInvariant();
        if (!LockPatterns.GitSha.IsMatch(sourceSha))
        {
            errors.Add("--source-sha must be a full 40-hex commit SHA");
        }
        else if (!string.Equals(sourceSha, git.HeadSha, StringComparison.Ordinal))
        {
            errors.Add($"--source-sha {sourceSha} is not the checked-out commit {git.HeadSha}; check out the commit you are releasing");
        }

        var repository = request.SourceRepository ?? descriptor.Source?.Repository;
        if (repository is null || !LockPatterns.Repository.IsMatch(repository))
        {
            errors.Add("the source repository is required as owner/name: set source.repository in the descriptor or pass --source-repository");
        }

        var runtimeImage = request.RuntimeImage ?? descriptor.RuntimeImage;
        if (runtimeImage is null || !LockPatterns.ImageReference.IsMatch(runtimeImage))
        {
            errors.Add("the runtime image is required as registry/repository@sha256:<64 hex>: set runtimeImage in the descriptor or pass --runtime-image");
        }

        else
        {
            var validatedImageDigest = runtimeImage[(runtimeImage.IndexOf('@', StringComparison.Ordinal) + 1)..];
            foreach (var target in descriptor.Targets!)
            {
                if (target.WorkerImageDigest is not null && target.WorkerImageDigest != validatedImageDigest)
                {
                    errors.Add($"targets[{target.Kind}].workerImageDigest must match the receipt-validated runtime image digest");
                }
            }
        }

        var generatedAt = request.GeneratedAt ?? git.CommitTimeUtc;
        if (!LockPatterns.TryParseGeneratedAt(generatedAt, out var modificationTime))
        {
            errors.Add("--generated-at must be yyyy-MM-ddTHH:mm:ssZ");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        if (IsWithin(outputDirectory, projectDirectory))
        {
            errors.Add("the output directory must not contain the project directory");
        }

        var includes = ResolveIncludes(descriptor, projectDirectory, outputDirectory, errors);
        var sourcePaths = includes.Select(p => p.Absolute).Append(descriptorPath).ToList();
        var dependencyLockPath = ResolveProjectFile(projectDirectory, descriptor.DependencyLock!, "dependencyLock", errors);
        var inputSchemaPath = ResolveProjectFile(projectDirectory, contract.InputSchema!, "process.contract.inputSchema", errors);
        var outputSchemaPath = ResolveProjectFile(projectDirectory, contract.OutputSchema!, "process.contract.outputSchema", errors);
        sourcePaths.AddRange(new[] { dependencyLockPath, inputSchemaPath, outputSchemaPath }.OfType<string>());
        var receipts = new List<GpReleaseValidationReceipt>();
        foreach (var receipt in descriptor.Validation!.Receipts!)
        {
            var receiptPath = ResolveProjectFile(projectDirectory, receipt.Path!, $"validation.receipts[{receipt.Name}]", errors);
            if (receiptPath is not null)
            {
                // Receipts are produced after the source commit and must not become source inputs
                // (binding a committed receipt to its own commit would be self-referential).
                if (sourcePaths.Contains(receiptPath, StringComparer.Ordinal))
                {
                    errors.Add($"validation.receipts[{receipt.Name}] must not be included in the source artifact or other release inputs");
                }
                var receiptDigest = GpValidationReceipt.Validate(receiptPath, receipt.Name!, sourceSha,
                    runtimeImage is null ? string.Empty : runtimeImage[(runtimeImage.IndexOf('@', StringComparison.Ordinal) + 1)..], errors);
                if (receiptDigest is null)
                {
                    continue;
                }
                receipts.Add(new GpReleaseValidationReceipt
                {
                    Name = receipt.Name!,
                    Sha256 = receiptDigest,
                    Status = "pass",
                });
            }
        }

        var dirty = git.DirtyPaths(sourcePaths);
        if (dirty.Length > 0)
        {
            errors.Add("the checkout has uncommitted changes under the release inputs; commit them first: " + string.Join(", ", dirty.Take(8)));
        }

        if (errors.Count > 0)
        {
            throw new GpReleaseException(errors);
        }

        var image = runtimeImage!;
        var runtimeDigest = image[(image.IndexOf('@', StringComparison.Ordinal) + 1)..];
        var artifactName = $"{process.Name}-{process.Version}.tar.gz";
        var artifactsDirectory = Path.Join(outputDirectory, ArtifactsDirectoryName);
        Directory.CreateDirectory(artifactsDirectory);
        var artifactPath = Path.Join(artifactsDirectory, artifactName);
        var artifactBytes = PackArtifact(includes, modificationTime);
        File.WriteAllBytes(artifactPath, artifactBytes);

        var lockDocument = new GpProcessReleaseLock
        {
            Process = new GpReleaseProcess
            {
                Name = process.Name!,
                Version = process.Version!,
                Runtime = process.Runtime!,
                Entrypoint = process.Entrypoint!.Trim(),
                Contract = new GpReleaseContract
                {
                    InputSchemaSha256 = GpProcessReleaseLock.Sha256Of(File.ReadAllBytes(inputSchemaPath!)),
                    OutputSchemaSha256 = GpProcessReleaseLock.Sha256Of(File.ReadAllBytes(outputSchemaPath!)),
                },
            },
            Source = new GpReleaseSource
            {
                Repository = repository!,
                Sha = sourceSha,
                DescriptorPath = git.RepositoryRelative(descriptorPath),
            },
            DependencyLock = new GpReleaseFileDigest
            {
                Path = git.RepositoryRelative(dependencyLockPath!),
                Sha256 = GpProcessReleaseLock.Sha256Of(File.ReadAllBytes(dependencyLockPath!)),
            },
            RuntimeImage = new GpReleaseRuntimeImage { Reference = image, Digest = runtimeDigest },
            WorkflowVersion = descriptor.WorkflowVersion!,
            ConfigurationReferences = (descriptor.Configuration ?? [])
                .Select(c => new GpReleaseConfigurationReference { Name = c.Name!, Reference = c.Reference! })
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToArray(),
            Targets = descriptor.Targets!
                .Select(t => new GpReleaseTarget
                {
                    Kind = t.Kind!,
                    WorkerImageDigest = t.WorkerImageDigest ?? runtimeDigest,
                    ChartVersion = t.ChartVersion,
                    JobDefinitionName = t.JobDefinitionName,
                })
                .ToArray(),
            Compatibility = new GpReleaseCompatibility
            {
                ServerContractVersion = new GpReleaseContractWindow
                {
                    Minimum = descriptor.Compatibility!.ServerContractVersion!.Minimum,
                    Maximum = descriptor.Compatibility.ServerContractVersion.Maximum,
                },
            },
            Artifacts =
            [
                new GpReleaseArtifact
                {
                    Name = artifactName,
                    Sha256 = GpProcessReleaseLock.Sha256Of(artifactBytes),
                    SizeBytes = artifactBytes.LongLength,
                },
            ],
            ValidationReceipts = receipts,
            GeneratedAt = generatedAt,
        };

        var validation = lockDocument.Validate();
        if (validation.Count > 0)
        {
            throw new GpReleaseException(validation);
        }

        var lockPath = Path.Join(outputDirectory, LockFileName);
        File.WriteAllText(lockPath, lockDocument.ToIndentedJson());
        return new GpReleaseResult { Lock = lockDocument, LockPath = lockPath, ArtifactPaths = [artifactPath] };
    }

    public static IReadOnlyList<string> VerifyArtifacts(GpProcessReleaseLock lockDocument, string artifactsDirectory)
    {
        ArgumentNullException.ThrowIfNull(lockDocument);
        var findings = new List<string>();
        foreach (var artifact in lockDocument.Artifacts)
        {
            var path = Path.Join(artifactsDirectory, artifact.Name);
            if (!File.Exists(path))
            {
                findings.Add($"{artifact.Name}: missing");
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var actual = GpProcessReleaseLock.Sha256Of(bytes);
            if (actual != artifact.Sha256 || bytes.LongLength != artifact.SizeBytes)
            {
                findings.Add($"{artifact.Name}: {actual} ({bytes.LongLength} bytes) does not match the lock");
            }
        }

        return findings;
    }

    private static List<(string Relative, string Absolute)> ResolveIncludes(GpProcessDescriptor descriptor, string projectDirectory, string outputDirectory, List<string> errors)
    {
        var roots = descriptor.Artifacts?.Include is { Count: > 0 } include ? include : ["."];
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var absolute = Path.GetFullPath(Path.Join(projectDirectory, root));
            if (!ValidateInputPath(projectDirectory, absolute, $"artifacts.include entry '{root}'", errors))
            {
                continue;
            }

            if (IsWithin(outputDirectory, absolute))
            {
                continue;
            }

            if (File.Exists(absolute))
            {
                files[ToRelative(projectDirectory, absolute)] = absolute;
            }
            else if (Directory.Exists(absolute))
            {
                foreach (var file in EnumerateFiles(absolute, projectDirectory, outputDirectory, errors))
                {
                    files[ToRelative(projectDirectory, file)] = file;
                }
            }
            else
            {
                errors.Add($"artifacts.include entry '{root}' does not exist");
            }
        }

        if (files.Count == 0)
        {
            errors.Add("the release artifact would be empty; check artifacts.include");
        }

        return files.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string projectDirectory, string outputDirectory, List<string> errors)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsWithin(outputDirectory, entry))
            {
                continue;
            }

            if (!ValidateInputPath(projectDirectory, entry, $"artifact '{ToRelative(projectDirectory, entry)}'", errors))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (ExcludedDirectoryNames.Contains(Path.GetFileName(entry), StringComparer.Ordinal))
                {
                    continue;
                }

                foreach (var file in EnumerateFiles(entry, projectDirectory, outputDirectory, errors))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool ValidateInputPath(string projectDirectory, string absolute, string field, List<string> errors)
    {
        if (!IsWithin(projectDirectory, absolute))
        {
            errors.Add($"{field} leaves the project directory");
            return false;
        }

        if (!File.Exists(absolute) && !Directory.Exists(absolute))
        {
            errors.Add($"{field} does not exist");
            return false;
        }

        // Check every component before opening a file or traversing a directory.
        for (var current = absolute; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                errors.Add($"{field} uses a symlink or reparse point");
                return false;
            }

            if (current == projectDirectory)
            {
                break;
            }
        }

        return true;
    }

    private static string? ResolveProjectFile(string projectDirectory, string relative, string field, List<string> errors)
    {
        var absolute = Path.GetFullPath(Path.Join(projectDirectory, relative));
        if (!File.Exists(absolute))
        {
            errors.Add($"{field} '{relative}' does not exist");
            return null;
        }

        return ValidateInputPath(projectDirectory, absolute, $"{field} '{relative}'", errors) ? absolute : null;
    }

    private static string ToRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static byte[] PackArtifact(List<(string Relative, string Absolute)> files, DateTimeOffset modificationTime)
    {
        using var tarStream = new MemoryStream();
        using (var writer = new TarWriter(tarStream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (relative, absolute) in files)
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, relative)
                {
                    Uid = 0,
                    Gid = 0,
                    UserName = string.Empty,
                    GroupName = string.Empty,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = modificationTime,
                };
                using var content = File.OpenRead(absolute);
                entry.DataStream = content;
                writer.WriteEntry(entry);
            }
        }

        tarStream.Position = 0;
        using var gzStream = new MemoryStream();
        using (var gzip = new GZipStream(gzStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            tarStream.CopyTo(gzip);
        }

        return gzStream.ToArray();
    }

    private sealed class GitFacts
    {
        private readonly string _root;

        private GitFacts(string root, string headSha, string commitTimeUtc)
        {
            _root = root;
            HeadSha = headSha;
            CommitTimeUtc = commitTimeUtc;
        }

        public string HeadSha { get; }

        public string CommitTimeUtc { get; }

        public static GitFacts? Resolve(string directory)
        {
            var root = Run(directory, "rev-parse", "--show-toplevel");
            if (root is null)
            {
                return null;
            }

            var head = Run(directory, "rev-parse", "HEAD");
            var time = Run(directory, "show", "-s", "--format=%cI", "HEAD");
            if (head is null || time is null)
            {
                return null;
            }

            var instant = DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
            return new GitFacts(Path.GetFullPath(root), head.ToLowerInvariant(), GpProcessReleaseLock.FormatGeneratedAt(instant));
        }

        public string RepositoryRelative(string path) => ToRelative(_root, Path.GetFullPath(path));

        public string[] DirtyPaths(IEnumerable<string> paths)
        {
            var arguments = new List<string> { "status", "--porcelain", "--untracked-files=all", "--" };
            arguments.AddRange(paths.Select(p => Path.GetFullPath(p)));
            var output = Run(_root, arguments.ToArray());
            if (string.IsNullOrWhiteSpace(output))
            {
                return [];
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
                .ToArray();
        }

        private static string? Run(string directory, params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 ? stdout.Trim() : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return null;
            }
        }
    }
}
