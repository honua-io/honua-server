// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Example service demonstrating the validation framework usage patterns.
/// This file shows before/after examples of constructor validation consolidation.
/// </summary>
internal sealed partial class ExampleRefactoredService
{
    // BEFORE: Traditional approach with duplicate null checks (OLD PATTERN)
    /*
    public ExampleRefactoredService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<ExampleRefactoredService> logger,
        IOptions<SomeOptions> options,
        ISomeService someService,
        IAnotherService anotherService)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _someService = someService ?? throw new ArgumentNullException(nameof(someService));
        _anotherService = anotherService ?? throw new ArgumentNullException(nameof(anotherService));
    }
    */

    // AFTER: Validation framework approach (NEW PATTERN)
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<ExampleRefactoredService> _logger;
    private readonly SomeOptions _options;
    private readonly ISomeService _someService;
    private readonly IAnotherService _anotherService;

    public ExampleRefactoredService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<ExampleRefactoredService> logger,
        IOptions<SomeOptions> options,
        ISomeService someService,
        IAnotherService anotherService)
    {
        // Validation framework eliminates 5 lines of duplicate null checks
        _connectionProvider = connectionProvider.ThrowIfNull();
        _logger = logger.ThrowIfNull();
        _options = options.ValidateAndGetValue();
        _someService = someService.ThrowIfNull();
        _anotherService = anotherService.ThrowIfNull();
    }

    // Alternative approach using specialized helper for common patterns:
    /*
    public ExampleRefactoredService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<ExampleRefactoredService> logger,
        IOptions<SomeOptions> options,
        ISomeService someService,
        IAnotherService anotherService)
    {
        // Single call validates the most common pattern
        var (validatedConnectionProvider, validatedLogger, validatedOptions) =
            ServiceValidationHelpers.ValidateServiceDependencies<ExampleRefactoredService, SomeOptions>(
                connectionProvider, logger, options);

        _connectionProvider = validatedConnectionProvider;
        _logger = validatedLogger;
        _options = validatedOptions;
        _someService = someService.ThrowIfNull();
        _anotherService = anotherService.ThrowIfNull();
    }
    */

    // Example method showing the service in use
    public async Task<string> DoSomethingAsync(CancellationToken cancellationToken = default)
    {
        LogExampleMethodCalled(_logger, _options.TimeoutSeconds);

        using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);

        var result1 = await _someService.ProcessAsync(cancellationToken);
        var result2 = await _anotherService.TransformAsync(result1, cancellationToken);

        return result2;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Example method called with timeout: {Timeout}")]
    private static partial void LogExampleMethodCalled(ILogger logger, int timeout);

    // Example options class
    public sealed class SomeOptions
    {
        public int TimeoutSeconds { get; set; } = 30;
        public string ConnectionString { get; set; } = string.Empty;
    }

    // Example service interfaces for demonstration
    public interface ISomeService
    {
        Task<string> ProcessAsync(CancellationToken cancellationToken = default);
    }

    public interface IAnotherService
    {
        Task<string> TransformAsync(string input, CancellationToken cancellationToken = default);
    }
}

/// <summary>
/// Example showing inheritance from ValidatedServiceBase for classes that prefer base class approach.
/// </summary>
internal sealed class ExampleInheritedService : ValidatedServiceBase
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<ExampleInheritedService> _logger;
    private readonly string _configValue;

    public ExampleInheritedService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<ExampleInheritedService> logger,
        string configValue)
    {
        // Using ValidatedServiceBase methods
        _connectionProvider = ValidateRequired(connectionProvider);
        _logger = ValidateRequired(logger);
        _configValue = ValidateNotEmpty(configValue);
    }

    // Alternative using fluent builder pattern:
    /*
    public ExampleInheritedService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<ExampleInheritedService> logger,
        string configValue)
    {
        // Fluent validation for multiple parameters
        Validate()
            .Required(connectionProvider)
            .Required(logger)
            .NotEmpty(configValue);

        _connectionProvider = connectionProvider;
        _logger = logger;
        _configValue = configValue;
    }
    */
}

/// <summary>
/// Example showing complex dependency validation for services with many injected dependencies.
/// </summary>
internal sealed class ExampleComplexService
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<ExampleComplexService> _logger;
    private readonly IServiceA _serviceA;
    private readonly IServiceB _serviceB;
    private readonly IServiceC _serviceC;
    private readonly IServiceD _serviceD;

    public ExampleComplexService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<ExampleComplexService> logger,
        IServiceA serviceA,
        IServiceB serviceB,
        IServiceC serviceC,
        IServiceD serviceD)
    {
        // Validation framework handles complex dependency graphs cleanly
        var (validatedConnectionProvider, validatedLogger) =
            ServiceValidationHelpers.ValidateServiceDependencies(connectionProvider, logger);

        var (validatedServiceA, validatedServiceB, validatedServiceC, validatedServiceD) =
            ValidationExtensions.ValidateConstructorParameters(serviceA, serviceB, serviceC, serviceD);

        _connectionProvider = validatedConnectionProvider;
        _logger = validatedLogger;
        _serviceA = validatedServiceA;
        _serviceB = validatedServiceB;
        _serviceC = validatedServiceC;
        _serviceD = validatedServiceD;
    }

    // Example service interfaces
    public interface IServiceA;
    public interface IServiceB;
    public interface IServiceC;
    public interface IServiceD;
}
