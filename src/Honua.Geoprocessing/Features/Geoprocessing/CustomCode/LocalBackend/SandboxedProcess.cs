// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Linq;

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
        // ulimit -f is specified in 512-byte blocks (POSIX; bash/dash/ash agree).
        var fileSizeBlocks = options.MaxOutputFileBytes is { } fileBytes && fileBytes > 0
            ? Math.Max(1, fileBytes / 512)
            : (long?)null;
        var sandboxUser = string.IsNullOrWhiteSpace(options.SandboxUser) ? null : options.SandboxUser.Trim();
        // RLIMIT_NPROC (ulimit -u) is enforced per REAL UID across the WHOLE host on Linux. Applying it
        // without dropping to a distinct sandbox UID would count against honua-server's own already-large
        // thread/process footprint and could break launches outright — so it is applied ONLY alongside
        // SandboxUser (see CustomCodeLocalBackendOptions remarks).
        var processCount = sandboxUser is not null && options.MaxProcessCount is { } procs && procs > 0
            ? (long?)procs
            : null;

        if (sandboxUser is not null)
        {
            // Defense in depth even though the options validator already constrains this charset: never
            // let an unvalidated SandboxUser value (e.g. a hand-built options object bypassing the
            // registered validator, as tests may do) reach shell interpolation below.
            ValidateSandboxUserCharset(sandboxUser);
        }

        var hasAnyPosixControl = cpuSeconds is not null || addressSpaceKib is not null
            || fileSizeBlocks is not null || sandboxUser is not null;

        if (!OperatingSystem.IsWindows() && hasAnyPosixControl)
        {
            // POSIX hosts (the real deployment/CI target): apply RLIMIT_CPU / RLIMIT_AS / RLIMIT_FSIZE
            // through a ulimit shell wrapper the kernel enforces, then (when configured) drop to the
            // distinct SandboxUser via setpriv before the target program runs. The wrapper never
            // interpolates the executable or its arguments — they are passed as positional parameters
            // ("$0"/"$@") and reached via `exec`, so there is no injection surface and the shell (and,
            // when present, setpriv) is replaced by the target process, keeping the process tree flat
            // for the kill path. Each ulimit call is followed by "|| exit 97": a limit the kernel refuses
            // (e.g. above the process's hard ceiling) now ABORTS the launch instead of silently
            // proceeding unconfined — the previous "2>/dev/null; " form swallowed such failures.
            var lines = new List<string>();
            if (addressSpaceKib is { } kib)
            {
                lines.Add(FormattableString.Invariant($"ulimit -v {kib} || exit 97"));
            }

            if (cpuSeconds is { } secs)
            {
                lines.Add(FormattableString.Invariant($"ulimit -t {secs} || exit 97"));
            }

            if (fileSizeBlocks is { } blocks)
            {
                lines.Add(FormattableString.Invariant($"ulimit -f {blocks} || exit 97"));
            }

            if (processCount is { } maxProcs)
            {
                lines.Add(FormattableString.Invariant($"ulimit -u {maxProcs} || exit 97"));
            }

            // --ambient-caps=-all --bounding-set=-all (adversarial review round 2): --clear-groups /
            // --no-new-privs / a bare reuid do NOT clear ambient or inheritable capabilities, and ambient
            // capabilities SURVIVE execve. If honua-server holds CAP_SETUID via an ambient grant (the one
            // practical way to hand that single capability to an already-non-root process, e.g. a systemd
            // unit's AmbientCapabilities=CAP_SETUID), that capability would otherwise pass straight through
            // into the dropped-privilege child, letting the "sandboxed" script setuid(0) back to root.
            // Explicitly stripping the ambient set and bounding set closes this regardless of how the
            // parent acquired CAP_SETUID (root, ambient grant, or otherwise).
            lines.Add(sandboxUser is not null
                ? FormattableString.Invariant(
                    $"exec {ResolveSetprivExecutable(options)} --reuid={sandboxUser} --regid={sandboxUser} --clear-groups --no-new-privs --ambient-caps=-all --bounding-set=-all -- \"$0\" \"$@\"")
                : "exec \"$0\" \"$@\"");

            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(string.Join("; ", lines));
            startInfo.ArgumentList.Add(spec.Executable); // becomes $0
            foreach (var argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument); // become $1, $2, ...
            }
        }
        else
        {
            // Non-POSIX host (or no limits configured): launch directly. CPU/address-space/UID-drop
            // controls are NOT enforced in-process here — run inside a cgroup-constrained container on
            // such hosts.
            startInfo.FileName = spec.Executable;
            foreach (var argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static string ResolveSetprivExecutable(CustomCodeLocalBackendOptions options)
        => string.IsNullOrWhiteSpace(options.SetprivExecutable) ? "setpriv" : options.SetprivExecutable;

    /// <summary>
    /// Restricts <see cref="CustomCodeLocalBackendOptions.SandboxUser"/> to a plain POSIX username
    /// charset before it is interpolated into the launch wrapper's shell script. The options validator
    /// enforces the same rule at startup; this is a second, load-bearing check so a hand-built options
    /// object (bypassing DI validation) can never smuggle a shell metacharacter into the wrapper.
    /// </summary>
    private static void ValidateSandboxUserCharset(string sandboxUser)
    {
        if (sandboxUser.Length == 0 ||
            !sandboxUser.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new InvalidOperationException(
                "CustomCodeLocalBackendOptions.SandboxUser must be a plain POSIX username (letters/digits/-/_/.) with no path or shell metacharacters.");
        }
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
