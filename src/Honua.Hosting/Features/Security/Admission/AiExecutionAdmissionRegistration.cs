// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Infrastructure.Security.Admission;

/// <summary>Composes shared authoritative AI admission without initializing any ledger at startup.</summary>
public static class AiExecutionAdmissionRegistration
{
    /// <summary>Registers a coordinator and one selected backend alias for both invocation and typed control paths.</summary>
    public static IServiceCollection AddAiExecutionAdmission(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiExecutionAdmissionOptions>()
            .Bind(configuration.GetSection(AiExecutionAdmissionOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BackendSelection>();
        services.TryAddKeyedSingleton<IAiExecutionAdmissionStore>("redis", (sp, _) =>
            new RedisAiExecutionAdmissionStore(sp.GetRequiredService<IConnectionMultiplexer>()));
        services.TryAddScoped<IAiExecutionAdmissionStore>(sp => sp.GetRequiredService<BackendSelection>().Resolve(sp));
        services.TryAddSingleton<IAiExecutionAdmission, AiExecutionAdmissionCoordinator>();
        services.TryAddSingleton<AiExecutionContextFactory>();
        return services;
    }

    private sealed class BackendSelection
    {
        private readonly string? _key;

        public BackendSelection(IOptions<AiExecutionAdmissionOptions> options,
            IServiceProviderIsKeyedService keyed, IServiceProviderIsService unkeyed)
        {
            var configured = options.Value.Backend?.ToLowerInvariant();
            _key = configured switch
            {
                "postgres" => keyed.IsKeyedService(typeof(IAiExecutionAdmissionStore), "postgres") ? "postgres" : null,
                "redis" => unkeyed.IsService(typeof(IConnectionMultiplexer)) ? "redis" : null,
                "auto" => keyed.IsKeyedService(typeof(IAiExecutionAdmissionStore), "postgres") ? "postgres"
                    : unkeyed.IsService(typeof(IConnectionMultiplexer)) ? "redis" : null,
                _ => null
            };
        }

        public IAiExecutionAdmissionStore Resolve(IServiceProvider services)
            => _key is null ? UnavailableStore.Instance : services.GetRequiredKeyedService<IAiExecutionAdmissionStore>(_key);
    }

    private sealed class UnavailableStore : IAiExecutionAdmissionStore
    {
        public static UnavailableStore Instance { get; } = new();
        public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, CancellationToken cancellationToken)
            => Task.FromResult(new AiAdmissionDecision("unavailable"));
        public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new AiAdmissionDecision("unavailable"));
        public Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken cancellationToken) => Task.FromResult("unavailable");
        public Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AiAdmissionState?> GetStateAsync(string deploymentId, CancellationToken cancellationToken)
            => Task.FromException<AiAdmissionState?>(new AiAdmissionException("unavailable"));
        public Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult(new AiAdmissionControlResult("unavailable", null));
    }
}
