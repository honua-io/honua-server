// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.ControlPlane;

namespace Honua.Worker.Gdal.Execution;

/// <summary>Image-bound, opt-in actual ELF entry pause; this is not mid-operation GDAL proof.</summary>
internal sealed class NativeEntryQualification
{
    internal const string LibraryPath = "/app/libhonua_gp_entry.so";
    private readonly string _record;
    private readonly string _release;
    private readonly string _nonce;
    private readonly string _executable;

    private readonly TimeSpan _timeout;

    internal NativeEntryQualification(string record, string release, string nonce, string executable, TimeSpan? timeout = null)
    {
        (_record, _release, _nonce, _executable) = (record, release, nonce, executable);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    internal static NativeEntryQualification? Prepare(ProcessStartInfo startInfo)
    {
        if (!ExecutionQualificationBarrier.Enabled || ExecutionQualificationBarrier.CurrentOperationId is not { } operation ||
            Environment.GetEnvironmentVariable(ExecutionQualificationBarrier.PauseAtEnvironmentVariable) != "native-process-started")
        {
            return null;
        }

        if (!OperatingSystem.IsLinux() || Path.GetFileName(startInfo.FileName) != "ogr2ogr")
        {
            throw new InvalidOperationException("Native entry qualification requires the image's native ogr2ogr executable.");
        }
        ValidateLoaderEnvironment(startInfo);
        var candidates = new[] { "/usr/bin/ogr2ogr", "/usr/local/bin/ogr2ogr" };
        var tool = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("The native entry executable is unavailable.");
        if (startInfo.FileName != "ogr2ogr" && !candidates.Contains(startInfo.FileName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Native entry qualification refuses a caller-selected executable path.");
        }
        var executable = new FileInfo(tool).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? tool;
        if (!candidates.Contains(executable, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The resolved executable is outside the supported native entry image layout.");
        }
        using (var binary = File.OpenRead(executable))
        {
            Span<byte> magic = stackalloc byte[4];
            if (binary.Read(magic) != 4 || !magic.SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }))
            {
                throw new InvalidOperationException("Native entry qualification requires an ELF executable.");
            }
        }
        if (!File.Exists(LibraryPath) || new FileInfo(LibraryPath).LinkTarget is not null ||
            (File.GetUnixFileMode(LibraryPath) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException("The immutable native entry shim is unavailable.");
        }
        var root = Environment.GetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable)!;
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != Path.TrimEndingDirectorySeparator(root))
        {
            throw new InvalidOperationException("Native entry qualification requires a canonical barrier root.");
        }
        var safe = operation.Replace("/", "_", StringComparison.Ordinal).Replace("\\", "_", StringComparison.Ordinal)
            .Replace("..", "_", StringComparison.Ordinal);
        for (DirectoryInfo? ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.LinkTarget is not null)
            {
                throw new InvalidOperationException("Native entry qualification refuses linked barrier roots.");
            }
        }
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("Native entry qualification requires a prepared barrier root.");
        }
        var directory = Path.Join(root, safe);
        Directory.CreateDirectory(directory);
        for (DirectoryInfo? current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null)
            {
                throw new InvalidOperationException("Native entry qualification refuses linked barrier directories.");
            }
        }
        var record = Path.Join(directory, "native-process-started.entry.json");
        var release = Path.Join(directory, "native-process-started.entry.release");
        if (Path.Exists(record) || Path.Exists(release))
        {
            throw new InvalidOperationException("Native entry qualification refuses stale handshake files.");
        }
        var nonce = Guid.NewGuid().ToString("N");
        // The parent environment is untouched. No configuration, job or caller selects a loader path.
        startInfo.FileName = executable;
        startInfo.Environment["LD_PRELOAD"] = LibraryPath;
        startInfo.Environment["HONUA_GP_NATIVE_ENTRY_RECORD"] = record;
        startInfo.Environment["HONUA_GP_NATIVE_ENTRY_RELEASE"] = release;
        startInfo.Environment["HONUA_GP_NATIVE_ENTRY_NONCE"] = nonce;
        return new NativeEntryQualification(record, release, nonce, executable);
    }

    internal static void ValidateLoaderEnvironment(ProcessStartInfo startInfo)
    {
        // This exact, immutable image directory is required by the PDAL image.
        // Preserve its value; qualification preflight forbids mounts over /opt.
        // Any extra search directory or other loader override is untrusted.
        if (startInfo.Environment.Any(pair =>
            (pair.Key.StartsWith("LD_", StringComparison.Ordinal) &&
             (pair.Key != "LD_LIBRARY_PATH" || pair.Value != "/opt/pdal/lib")) ||
            pair.Key.StartsWith("HONUA_GP_NATIVE_ENTRY_", StringComparison.Ordinal) || pair.Key == "GLIBC_TUNABLES"))
        {
            throw new InvalidOperationException("Native entry qualification refuses inherited loader or handshake overrides.");
        }
    }

    internal async Task WaitAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        while (!File.Exists(_record))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The native child exited before the entry handshake.");
            }
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
        }
        timeout.Token.ThrowIfCancellationRequested();
        if (new FileInfo(_record).LinkTarget is not null || new FileInfo(_record).Length > 16384)
        {
            throw new InvalidOperationException("Native entry qualification refuses linked readiness.");
        }
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_record, timeout.Token).ConfigureAwait(false));
        var value = json.RootElement;
        var actualExecutable = new FileInfo($"/proc/{process.Id}/exe").ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        if (value.GetProperty("schema").GetString() != "honua.gp-native-entry/v1" ||
            value.GetProperty("boundary").GetString() != "native-exec-entry-paused" ||
            value.GetProperty("nonce").GetString() != _nonce || value.GetProperty("pid").GetInt32() != process.Id ||
            value.GetProperty("parentPid").GetInt32() != Environment.ProcessId ||
            value.GetProperty("executable").GetString() != _executable || actualExecutable != _executable || process.HasExited)
        {
            throw new InvalidOperationException("The native entry handshake identity does not match the actual executable.");
        }
    }

    internal void Release()
    {
        var temporary = $"{_release}.{_nonce}.tmp";
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(System.Text.Encoding.ASCII.GetBytes(_nonce));
                stream.Flush(flushToDisk: true);
            }
            // The constructor can observe only the complete nonce. Never replace a
            // stale release, and never expose a zero-length destination while writing.
            File.Move(temporary, _release, overwrite: false);
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original release error; the runner kills/reaps the child.
                }
            }
        }
    }
}
