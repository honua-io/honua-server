// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;

namespace Honua.TestKit.Security;

/// <summary>Explicit invocation admission fixture for tests that exercise model transport without a durable backend.</summary>
public sealed class RecordingAiAdmission : IAiExecutionAdmission
{
    public int Acquired { get; private set; }
    public int Sent { get; private set; }
    public int Released { get; private set; }
    public string? DenialCode { get; set; }
    public Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DenialCode is { } denied) throw new AiAdmissionException(denied);
        if (!context.IsValid) throw new AiAdmissionException("invalid_context");
        Acquired++;
        return Task.FromResult<IAiExecutionPermit>(new Permit(this, cancellationToken));
    }

    public AiFinancialPriceBasis? FinancialBasis { get; set; }
    public Task<AiFinancialPriceBasis?> GetFinancialBasisAsync(AiExecutionContext context, CancellationToken cancellationToken)
        => Task.FromResult(FinancialBasis);
    public AiFinancialRequest? FinancialRequest { get; private set; }
    public List<AiFinancialUsage> FinancialUsage { get; } = [];
    public bool Financial { get; set; }
    public Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, AiFinancialRequest request, CancellationToken cancellationToken)
    {
        FinancialRequest = request;
        return AcquireAsync(context, cancellationToken);
    }

    public static AiExecutionContext Context(string useCase = "studio-chat")
        => AiExecutionContext.Create("test-deployment", "ApiKey:api-key:11111111-1111-1111-1111-111111111111", useCase);

    public static IAiExecutionPermit TransportPermit(CancellationToken cancellationToken = default)
        => new Permit(new RecordingAiAdmission(), cancellationToken, begun: true);

    private sealed class Permit(RecordingAiAdmission owner, CancellationToken token, bool begun = false) : IAiExecutionPermit
    {
        private int _begun = begun ? 1 : 0;
        private int _sent;
        private int _released;
        public CancellationToken Token => token;
        public bool HasFinancialReservation => owner.Financial;
        public Task RecordFinancialUsageAsync(AiFinancialUsage usage, CancellationToken cancellationToken)
        {
            owner.FinancialUsage.Add(usage);
            return Task.CompletedTask;
        }
        public Task BeginInvocationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _begun, 1) != 0) throw new AiAdmissionException("duplicate_invocation");
            return Task.CompletedTask;
        }
        public Task EnsureCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task AuthorizeSendAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_begun != 1 || Interlocked.Exchange(ref _sent, 1) != 0) throw new AiAdmissionException("duplicate_invocation");
            owner.Sent++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Released++;
            return ValueTask.CompletedTask;
        }
    }
}
