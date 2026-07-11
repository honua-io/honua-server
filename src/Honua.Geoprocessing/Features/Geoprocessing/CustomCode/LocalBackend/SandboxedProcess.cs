// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Honua.Geoprocessing.CustomCode.LocalBackend;

/// <summary>
/// The executable + ordered argument list a <see cref="ICustomCodeWorkloadPreparer"/> resolved for a
/// prepared checkout. Arguments are always passed as a real argument vector (never a shell command
/// string), so there is no shell word-splitting or injection surface even when the sandbox wraps the
/// launch in a <c>ulimit</c> shell.
/// </summary>
/// <param name="Executable">Absolute path to the interpreter/binary to launch.</param>
/// <param name="Arguments">Ordered arguments passed as an argv vector.</param>
internal readonly record struct SandboxLaunchSpec(string Executable, IReadOnlyList<string> Arguments);

/// <summary>
/// Builds the hardened <see cref="Process"/> for a custom-code launch. This is the security core of
/// the local backend: it clears the inherited environment, applies OS-level CPU/address-space limits
/// on POSIX hosts, roots the process in the single-use scratch directory, and never passes a shell
/// command string. Wall-clock enforcement and process-tree kill live in the backend's monitor.
/// </summary>
internal static class SandboxedProcess
{
    /// <summary>
    /// Creates (but does not start) the sandboxed process for <paramref name="spec"/>.
    /// </summary>
    /// <param name="spec">The resolved executable + argv.</param>
    /// <param name="workingDirectory">The single-use scratch directory (the only path handed to the child).</param>
    /// <param name="environment">
    /// The EXACT environment to expose. The returned process inherits nothing else: the parent
    /// environment is cleared and only these entries are set.
    /// </param>
    /// <param name="options">The backend options carrying the CPU/address-space limits.</param>
    public static Process Create(
        SandboxLaunchSpec spec,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CustomCodeLocalBackendOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closed immediately so a read-from-stdin script cannot hang the slot
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Environment allowlist: start from an EMPTY environment. ProcessStartInfo.Environment is
        // pre-populated with the parent (honua-server) environment — including any cloud credentials,
        // DB connection strings, and tokens present there — so we must clear it before adding only the
        // vetted entries. This is the hard guarantee that untrusted user code inherits no host secret.
        startInfo.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            if (!string.IsNullOrEmpty(name))
            {
                startInfo.Environment[name] = value ?? string.Empty;
            }
        }

        var cpuSeconds = options.MaxCpuTime is { } cpu && cpu > TimeSpan.Zero
            ? (long)Math.Ceiling(cpu.TotalSeconds)
            : (long?)null;
        var addressSpaceKib = options.MaxAddressSpaceBytes is { } mem && mem > 0
            ? Math.Max(1, mem / 1024)
            : (long?)null;

        if (!OperatingSystem.IsWindows() && (cpuSeconds is not null || addressSpaceKib is not null))
        {
            // POSIX hosts (the real deployment/CI target): apply RLIMIT_CPU / RLIMIT_AS through a
            // ulimit shell wrapper the kernel enforces. The wrapper never interpolates the executable
            // or its arguments — they are passed as positional parameters ("$0"/"$@") and reached via
            // `exec`, so there is no injection surface and the shell is replaced by the target process
            // (keeping the process tree flat for the kill path).
            var script = new StringBuilder();
            if (addressSpaceKib is { } kib)
            {
                script.Append(CultureInfo.InvariantCulture, $"ulimit -v {kib} 2>/dev/null; ");
            }

            if (cpuSeconds is { } secs)
            {
                script.Append(CultureInfo.InvariantCulture, $"ulimit -t {secs} 2>/dev/null; ");
            }

            script.Append("exec \"$0\" \"$@\"");

            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script.ToString());
            startInfo.ArgumentList.Add(spec.Executable); // becomes $0
            foreach (var argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument); // become $1, $2, ...
            }
        }
        else
        {
            // Non-POSIX host (or no limits configured): launch directly. CPU/address-space limits are
            // NOT enforced in-process here — run inside a cgroup-constrained container on such hosts.
            startInfo.FileName = spec.Executable;
            foreach (var argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }
}

/// <summary>
/// Path-containment guards for the local backend. Every filesystem path handed to (or constructed
/// for) the subprocess is validated to resolve strictly under the job's single-use scratch root, so
/// a crafted job id, repo layout, or relative path cannot escape the sandbox directory via
/// <c>..</c> traversal, an absolute path, or a symlink-shaped segment.
/// </summary>
internal static class SandboxPaths
{
    /// <summary>
    /// Resolves <paramref name="segment"/> under <paramref name="root"/> and throws when the result
    /// escapes <paramref name="root"/>. Returns the canonical full path on success.
    /// </summary>
    public static string ResolveContained(string root, string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, segment));

        if (!IsUnder(fullRoot, candidate))
        {
            throw new CustomCodePathEscapeException(
                $"Resolved path escapes the sandbox scratch directory (root='{fullRoot}').");
        }

        return candidate;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing canonicalized full paths.
    /// </summary>
    public static bool IsUnder(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullRoot, fullCandidate, comparison))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Reduces an operation id to a safe single directory segment (letters/digits/dash/underscore).
    /// Any other character becomes '-', and an empty result becomes "job", so the segment can never
    /// contain a path separator, drive marker, or traversal token.
    /// </summary>
    public static string SanitizeSegment(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return "job";
        }

        var chars = operationId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))
            {
                chars[i] = '-';
            }
        }

        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "job" : sanitized;
    }
}

/// <summary>Raised when a constructed subprocess path would escape the single-use scratch directory.</summary>
internal sealed class CustomCodePathEscapeException(string message) : Exception(message);
