// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Honua.ControlPlane;

namespace Honua.Server.Hosting.ComposeExecutor;

/// <summary>How <see cref="ComposeExecutorMode.SwitchKey"/> was set.</summary>
internal enum ComposeExecutorSwitch
{
    /// <summary>Unset or false. The process serves HTTP.</summary>
    NotRequested = 0,

    /// <summary>True. Serve the allowlisted Compose socket and do not build the web host.</summary>
    Requested = 1,

    /// <summary>A value that is neither true nor false.</summary>
    Invalid = 2
}

/// <summary>
/// Resolves <c>HONUA_COMPOSE_EXECUTOR</c>. A serving process never takes this path unless the
/// switch is set. Migrate-only is a different switch and wins when both are set, because that
/// path returns first.
/// </summary>
internal static class ComposeExecutorMode
{
    /// <summary>Configuration key and environment variable that selects executor mode.</summary>
    internal const string SwitchKey = "HONUA_COMPOSE_EXECUTOR";

    /// <summary>Reads the switch. Empty means <see cref="ComposeExecutorSwitch.NotRequested"/>.</summary>
    internal static ComposeExecutorSwitch Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[SwitchKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ComposeExecutorSwitch.NotRequested;
        }

        return raw.Trim() switch
        {
            var value when bool.TryParse(value, out var enabled) =>
                enabled ? ComposeExecutorSwitch.Requested : ComposeExecutorSwitch.NotRequested,
            "1" => ComposeExecutorSwitch.Requested,
            "0" => ComposeExecutorSwitch.NotRequested,
            _ => ComposeExecutorSwitch.Invalid
        };
    }
}

/// <summary>
/// Sidecar entry point. Binds a mode <c>0660</c> unix socket and serves one JSON request per
/// connection. Invalid configuration exits 2 with one JSON line and does not listen.
/// </summary>
internal static class ComposeExecutorCommand
{
    /// <summary>Called from <c>Program.cs</c> before the web host is built.</summary>
    internal static async Task<int> RunFromEntryPointAsync(IConfiguration configuration)
    {
        var mode = ComposeExecutorMode.Resolve(configuration);
        if (mode == ComposeExecutorSwitch.Invalid)
        {
            WriteFailure(ComposeExecutorCodes.ConfigUnsupported, "HONUA_COMPOSE_EXECUTOR is neither true nor false.");
            return 2;
        }

        var options = new ComposeExecutorOptions();
        configuration.GetSection(ComposeExecutorOptions.SectionName).Bind(options);
        var problem = options.ValidateForCommand();
        if (problem is { } invalid)
        {
            WriteFailure(invalid.Code, invalid.Message);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        try
        {
            return await ServeAsync(options, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>Binds the socket and serves until <paramref name="cancellationToken"/> is cancelled.</summary>
    internal static async Task<int> ServeAsync(ComposeExecutorOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var controller = new ComposeProjectController(options, new DockerComposeProcess());
        var socketPath = options.SocketPath;
        var directory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            WriteFailure(ComposeExecutorCodes.ExecutorUnconfigured, "The executor socket path has no directory.");
            return 2;
        }

        Directory.CreateDirectory(directory);
        if (Directory.Exists(socketPath))
        {
            WriteFailure(ComposeExecutorCodes.ExecutorUnconfigured, "The executor socket path is a directory.");
            return 1;
        }

        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                WriteFailure(ComposeExecutorCodes.ExecutorUnconfigured, "The executor socket requires a Unix host.");
                return 1;
            }

            File.SetUnixFileMode(
                socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        catch (SocketException)
        {
            WriteFailure(ComposeExecutorCodes.ExecutorUnconfigured, "The executor socket could not be bound.");
            return 1;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // One request per connection. The next accept waits so a slow docker call cannot overlap.
                await HandleAsync(controller, accepted, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(socketPath))
                {
                    File.Delete(socketPath);
                }
            }
            catch (IOException)
            {
                // The process is exiting. A leftover socket is removed on the next start.
            }
        }

        return 0;
    }

    private static async Task HandleAsync(
        ComposeProjectController controller,
        Socket accepted,
        CancellationToken cancellationToken)
    {
        using (accepted)
        {
            await using var stream = new NetworkStream(accepted, ownsSocket: false);
            var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            ComposeExecutorResponse response;
            if (line == null)
            {
                response = ComposeExecutorProtocol.Refuse(null, ComposeExecutorCodes.ScopeViolation, "The executor request is too long.");
            }
            else if (!ComposeExecutorProtocol.TryReadRequest(line, out var request, out var failure) || request == null)
            {
                response = failure ?? ComposeExecutorProtocol.Refuse(null, ComposeExecutorCodes.ConfigUnsupported, "The executor request was refused.");
            }
            else
            {
                try
                {
                    response = await controller.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    response = ComposeExecutorProtocol.Refuse(
                        request.RequestId,
                        ComposeExecutorCodes.ReadbackIncomplete,
                        "The executor request failed before readback completed.");
                }
            }

            var payload = Encoding.UTF8.GetBytes(ComposeExecutorProtocol.Serialize(response) + "\n");
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length <= ComposeExecutorProtocol.MaxRequestBytes)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (buffer.Length == ComposeExecutorProtocol.MaxRequestBytes)
            {
                return null;
            }

            buffer.WriteByte(one[0]);
        }

        return null;
    }

    private static void WriteFailure(string code, string message)
    {
        var response = ComposeExecutorProtocol.Refuse(null, code, message);
        Console.Out.WriteLine(ComposeExecutorProtocol.Serialize(response));
    }
}
