// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.Hosting;

// Lambda freezes the sandbox between invocations, so a host that has stopped listening is exited on the next thaw or the runtime keeps routing to a dead port.
internal sealed partial class LambdaHostStopExitGuard : IHostedService, IDisposable
{
    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    internal const string AwsLambdaRuntimeApiKey = "AWS_LAMBDA_RUNTIME_API";

    internal const int StalledStopExitCode = 1;

    internal static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(10);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LambdaHostStopExitGuard> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _drainGrace;
    private readonly Action<int> _exit;
    private CancellationTokenRegistration _stoppingRegistration;
    private int _armed;

    public LambdaHostStopExitGuard(IHostApplicationLifetime lifetime, ILogger<LambdaHostStopExitGuard> logger)
        : this(lifetime, logger, TimeProvider.System, DefaultDrainGrace, Environment.Exit)
    {
    }

    internal LambdaHostStopExitGuard(
        IHostApplicationLifetime lifetime,
        ILogger<LambdaHostStopExitGuard> logger,
        TimeProvider timeProvider,
        TimeSpan drainGrace,
        Action<int> exit)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _drainGrace = drainGrace;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    internal static bool IsAwsLambda(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !string.IsNullOrWhiteSpace(environment(AwsLambdaFunctionNameKey))
            || !string.IsNullOrWhiteSpace(environment(AwsLambdaRuntimeApiKey));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = _lifetime.ApplicationStopping.Register(static state => ((LambdaHostStopExitGuard)state!).Arm(), this);
        return Task.CompletedTask;
    }

    // An armed exit is left in place because the stop it guards is this one.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _stoppingRegistration.Dispose();

    private void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) != 0)
        {
            return;
        }

        LogArmed(_logger, _drainGrace.TotalSeconds);
        _ = ExitAfterDrainGraceAsync();
    }

    // A pending delay is rooted by the runtime timer queue, so the exit still fires after host
    // disposal drops this service while the stop is stalled.
    private async Task ExitAfterDrainGraceAsync()
    {
        await Task.Delay(_drainGrace, _timeProvider).ConfigureAwait(false);
        ExitStalledProcess();
    }

    private void ExitStalledProcess()
    {
        // Written straight to stderr as well: the logging pipeline may already be shutting down,
        // and this line is the only record of why the sandbox restarted.
        Console.Error.WriteLine(
            "Honua host stop did not finish within the Lambda drain grace; exiting so Lambda recycles the sandbox (honua-server#6558).");
        LogExiting(_logger, _drainGrace.TotalSeconds);
        _exit(StalledStopExitCode);
    }

    [LoggerMessage(EventId = 4149, Level = LogLevel.Warning,
        Message = "Host is stopping inside a Lambda sandbox; the process exits in {GraceSeconds}s unless the stop finishes first")]
    private static partial void LogArmed(ILogger logger, double graceSeconds);

    [LoggerMessage(EventId = 4150, Level = LogLevel.Critical,
        Message = "Host stop did not finish within {GraceSeconds}s inside a Lambda sandbox; exiting the process so Lambda recycles the sandbox")]
    private static partial void LogExiting(ILogger logger, double graceSeconds);
}
