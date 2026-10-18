// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Honua.Geoprocessing.Cli;

/// <summary>Installed transport/bootstrap entry point for the existing isolated GP runner.</summary>
internal static partial class GpWorkspaceCommand
{
    internal static IReadOnlyDictionary<string, byte[]> Resources()
    {
        const string prefix = "Workspace/";
        var assembly = typeof(GpWorkspaceCommand).Assembly;
        var resources = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var input = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            resources.Add(name[prefix.Length..], buffer.ToArray());
        }
        foreach (var required in new[] { "workspace.py", "workspace_scope.py", "workspace_service.py", "Dockerfile.workspace", "README.md" })
        {
            if (!resources.ContainsKey(required)) throw new InvalidOperationException("The installed devkit lacks its workspace payload.");
        }
        return resources;
    }

    internal static void Extract(string destination, Action<string, byte[]>? write = null)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new GpCliUsageException("Workspace payload destination must be empty.");
        }
        var resources = Resources();
        var manifest = new
        {
            schema = "honua.gp.workspace-payload/v1",
            devkitVersion = GpBundledToolchain.Version,
            files = resources.ToDictionary(pair => pair.Key, pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value))),
            image = "Requires a normally released digest-pinned sandbox image; no mutable tag or arbitrary dependency restoration is implied."
        };
        var files = resources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        files.Add("workspace-provenance.json", System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest) + "\n"));
        var existed = Directory.Exists(destination);
        Directory.CreateDirectory(destination);
        var attempted = new List<string>();
        try
        {
            foreach (var (name, bytes) in files)
            {
                var path = Path.Join(destination, name);
                if (write is not null)
                {
                    attempted.Add(path);
                    write(path, bytes);
                }
                else
                {
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    attempted.Add(path);
                    output.Write(bytes);
                }
            }
        }
        catch
        {
            foreach (var path in attempted) File.Delete(path);
            if (!existed) Directory.Delete(destination);
            throw;
        }
    }

    internal static string ResolvedDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new GpCliUsageException("Workspace directories must exist.");
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Join(current, segment));
            current = directory.LinkTarget is null ? directory.FullName :
                (directory.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException("Unresolved workspace directory link.")).FullName;
        }
        return current;
    }

    internal static ProcessStartInfo StartInfo(string payload, string[] arguments)
    {
        var result = new ProcessStartInfo("python3") { UseShellExecute = false };
        result.ArgumentList.Add(Path.Join(payload, "workspace_service.py"));
        foreach (var argument in arguments) result.ArgumentList.Add(argument);
        return result;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) throw new GpCliUsageException("Usage: workspace serve --project <dir> --runs <dir> --image sha256:<digest> | workspace bootstrap --output <empty-dir>");
        if (args[0] == "bootstrap")
        {
            if (args.Length != 3 || args[1] != "--output") throw new GpCliUsageException("Usage: workspace bootstrap --output <empty-dir>");
            Extract(Path.GetFullPath(args[2]));
            Console.WriteLine("Workspace payload and image recipe extracted. Supply reviewed toolchain/caches and qualify the released pinned image before use.");
            return 0;
        }
        if (args[0] != "serve") throw new GpCliUsageException("Unknown workspace command.");
        if (OperatingSystem.IsWindows()) throw new GpCliUsageException("Workspace serving requires a POSIX Docker host; use WSL on Windows.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--project", "--runs", "--image", "--port", "--bind-address", "--allowed-host" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || i + 1 >= args.Length) throw new GpCliUsageException("Unknown or missing workspace serve option.");
            if (args[i] != "--allowed-host" && values.ContainsKey(args[i])) throw new GpCliUsageException("Duplicate workspace serve option.");
            values[args[i]] = args[i + 1];
        }
        foreach (var required in new[] { "--project", "--runs", "--image" })
        {
            if (!values.ContainsKey(required)) throw new GpCliUsageException($"{required} is required.");
        }
        if ((Environment.GetEnvironmentVariable("HONUA_GP_WORKSPACE_KEY") ?? "").Length < 32)
        {
            throw new GpCliUsageException("Set the server/daemon service credential HONUA_GP_WORKSPACE_KEY (at least 32 characters); never pass it in command arguments.");
        }
        var root = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "honua", "gp-workspace");
        Directory.CreateDirectory(root);
        var version = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GpBundledToolchain.Version)))[..12];
        var payload = Directory.CreateDirectory(Path.Join(root, $"payload-{version}-{Guid.NewGuid():N}"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var project = ResolvedDirectory(values["--project"]);
            var actualPayload = ResolvedDirectory(payload.FullName);
            if (Path.GetRelativePath(project, actualPayload) is var relative && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                throw new GpCliUsageException("The installed workspace payload must be outside editable project source.");
            }
            Extract(payload.FullName);
            using var process = Process.Start(StartInfo(payload.FullName, args[1..])) ?? throw new InvalidOperationException("Could not start Python workspace service.");
            // Terminal SIGINT already reaches the child process group. Forwarding it
            // again could interrupt the daemon while it is cleaning up containers.
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => Forward(context, process, 15));
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            Directory.Delete(payload.FullName, recursive: true);
        }
    }

    private static void Forward(PosixSignalContext context, Process process, int signal)
    {
        context.Cancel = true;
        if (!process.HasExited) _ = SendSignal(process.Id, signal);
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
