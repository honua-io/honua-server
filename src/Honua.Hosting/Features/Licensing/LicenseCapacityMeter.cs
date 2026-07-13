// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Infrastructure.Licensing;

internal sealed partial class LicenseCapacityMeter : BackgroundService, ILicenseCapacityMeter
{
    private const string RegistrationScript = """
        local members = redis.call('SMEMBERS', KEYS[1])
        local current = 0
        for _, key in ipairs(members) do
            if key ~= KEYS[2] then
                local payload = redis.call('GET', key)
                if payload then
                    local first_sep = string.find(payload, '|')
                    local role_start = first_sep + 1
                    local second_sep = string.find(payload, '|', role_start)
                    local units = tonumber(string.sub(payload, 1, first_sep - 1)) or 0
                    local role = string.sub(payload, role_start, second_sep - 1)
                    if role == 'Production' then
                        current = current + units
                    end
                else
                    redis.call('SREM', KEYS[1], key)
                end
            end
        end

        local joining = tonumber(ARGV[1]) or 0
        local enforce = ARGV[2] == '1'
        local ceiling = tonumber(ARGV[3]) or 0
        if enforce and current + joining > ceiling then
            return { 0, current }
        end

        redis.call('SET', KEYS[2], ARGV[4], 'PX', ARGV[5])
        redis.call('SADD', KEYS[1], KEYS[2])
        return { 1, current + joining }
        """;

    private static readonly Counter<long> SurgeModeChanges = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.license.capacity.surge_mode_changes",
        unit: "{change}",
        description: "Declared licensing surge-mode changes.");

    private static readonly Counter<long> RegistrationRefusals = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.license.capacity.registration_refusals",
        unit: "{registration}",
        description: "Serving instance registrations refused by capacity-band enforcement.");

    private readonly ILicenseEntitlementService _licenseEntitlements;
    private readonly IOptions<LicenseCapacityOptions> _options;
    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LicenseCapacityMeter> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, LicenseCapacityInstance> _localInstances = new(StringComparer.Ordinal);
    private readonly List<LicenseCapacitySample> _localSamples = [];
    private DateTimeOffset? _localGraceStartedAt;
    private LocalSurgeState? _localSurge;
    private decimal _localSurgeUsedDays;
    private volatile bool _registeredSelf;
    private volatile bool _meteringGap;

    public LicenseCapacityMeter(
        ILicenseEntitlementService licenseEntitlements,
        IOptions<LicenseCapacityOptions> options,
        TimeProvider timeProvider,
        ILogger<LicenseCapacityMeter> logger,
        IConnectionMultiplexer? redis = null)
    {
        _licenseEntitlements = licenseEntitlements;
        _options = options;
        _redis = redis;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<LicenseCapacityRegistrationDecision> RegisterInstanceAsync(
        LicenseCapacityRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);

        var normalized = NormalizeRequest(request);
        var snapshot = _licenseEntitlements.GetSnapshot();
        var terms = snapshot.CapacityTerms;
        var preRegistrationState = await BuildStateAsync(
            terms,
            excludeInstanceId: normalized.InstanceId,
            updateGrace: true,
            cancellationToken).ConfigureAwait(false);

        var excluded = LicenseCapacityCalculator.IsExcludedRole(normalized.DeploymentRole);
        var joiningUnits = excluded ? 0m : normalized.ServingUnits;
        var shouldRefuse = !normalized.IsRefresh &&
            LicenseCapacityCalculator.ShouldRefuseRegistration(
                terms,
                preRegistrationState.CurrentServingUnits,
                joiningUnits,
                preRegistrationState.State,
                excluded);

        if (shouldRefuse)
        {
            RegistrationRefusals.Add(
                1,
                new KeyValuePair<string, object?>("honua.license.capacity.state", preRegistrationState.State.ToString()));
            // `terms` is provably non-null here: ShouldRefuseRegistration() only returns true
            // (making shouldRefuse true) when its `terms` argument is non-null. The `?.`/`??`
            // is kept anyway as a defensive guard against that contract changing in
            // LicenseCapacityCalculator, and removing it would require a null-forgiving
            // operator under Nullable=enable without changing behavior.
            LicenseCapacityMeterLog.RegistrationRefused(
                _logger,
                normalized.InstanceId,
                preRegistrationState.CurrentServingUnits,
                joiningUnits,
                terms?.MaxSustainedServingUnits ?? 0m,
                preRegistrationState.State);

            return new LicenseCapacityRegistrationDecision
            {
                IsAccepted = false,
                Reason = "Registration refused: serving capacity is above the active license band ceiling.",
                State = preRegistrationState
            };
        }

        var accepted = await UpsertHeartbeatAsync(
            normalized,
            preRegistrationState,
            enforce: !normalized.IsRefresh &&
                terms is not null &&
                !excluded &&
                preRegistrationState.State is not LicenseCapacityBandState.NotConfigured and
                    not LicenseCapacityBandState.Excluded and
                    not LicenseCapacityBandState.Surge and
                    not LicenseCapacityBandState.MeteringGap,
            cancellationToken).ConfigureAwait(false);

        if (!accepted)
        {
            var current = await BuildStateAsync(
                terms,
                excludeInstanceId: normalized.InstanceId,
                updateGrace: false,
                cancellationToken).ConfigureAwait(false);
            RegistrationRefusals.Add(
                1,
                new KeyValuePair<string, object?>("honua.license.capacity.state", current.State.ToString()));
            LicenseCapacityMeterLog.RegistrationRefused(
                _logger,
                normalized.InstanceId,
                current.CurrentServingUnits,
                joiningUnits,
                terms?.MaxSustainedServingUnits ?? 0m,
                current.State);

            return new LicenseCapacityRegistrationDecision
            {
                IsAccepted = false,
                Reason = "Registration refused: concurrent scale-up exceeded the active license band ceiling.",
                State = current
            };
        }

        var state = await GetCapacityStateAsync(cancellationToken).ConfigureAwait(false);
        return new LicenseCapacityRegistrationDecision
        {
            IsAccepted = true,
            Reason = state.MeteringGap
                ? "Registration accepted with capacity enforcement suspended because the coordinated meter is unavailable."
                : "Registration accepted.",
            State = state
        };
    }

    public async Task<LicenseCapacityState> GetCapacityStateAsync(CancellationToken cancellationToken = default)
    {
        var terms = _licenseEntitlements.GetSnapshot().CapacityTerms;
        return await BuildStateAsync(
            terms,
            excludeInstanceId: null,
            updateGrace: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LicenseCapacityState> SetSurgeModeAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (UseRedis)
        {
            try
            {
                var database = _redis!.GetDatabase();
                if (enabled)
                {
                    await database.StringSetAsync(
                        SurgeKey,
                        FormatSurgePayload(now, reason),
                        flags: CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                else
                {
                    var active = await ReadRedisSurgeAsync(database, now).ConfigureAwait(false);
                    if (active?.IsActive == true && active.ActivatedAt.HasValue)
                    {
                        var usedDays = Math.Max(0m, (decimal)(now - active.ActivatedAt.Value).TotalDays);
                        await database.StringIncrementAsync(
                            SurgeUsageKey(now),
                            (double)usedDays,
                            CommandFlags.DemandMaster).ConfigureAwait(false);
                    }

                    await database.KeyDeleteAsync(SurgeKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                }

                _meteringGap = false;
            }
            catch (Exception ex) when (IsRedisException(ex))
            {
                MarkMeteringGap(ex);
                SetLocalSurge(enabled, reason, now);
            }
        }
        else
        {
            SetLocalSurge(enabled, reason, now);
        }

        SurgeModeChanges.Add(
            1,
            new KeyValuePair<string, object?>("honua.license.surge.enabled", enabled));
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "honua.license.capacity.surge",
            ActivityKind.Internal);
        activity?.SetTag("honua.license.surge.enabled", enabled);
        activity?.SetTag("honua.license.surge.reason_present", !string.IsNullOrWhiteSpace(reason));
        LicenseCapacityMeterLog.SurgeModeChanged(_logger, enabled, reason);

        return await GetCapacityStateAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.RegistrationEnabled)
        {
            return;
        }

        // Perform the initial registration here rather than in StartAsync so that a
        // slow/unreachable Redis cannot stall host startup before the web server begins
        // listening. RegisterInstanceAsync already falls back to local metering on Redis
        // failures (see UpsertHeartbeatAsync); a genuine capacity-band refusal is surfaced
        // here instead of aborting host startup.
        var registration = await RegisterInstanceAsync(BuildLocalRequest(isRefresh: false), stoppingToken)
            .ConfigureAwait(false);
        if (!registration.IsAccepted)
        {
            LicenseCapacityMeterLog.RegistrationStartupRefused(_logger, registration.Reason);
        }
        else
        {
            _registeredSelf = true;
        }

        await RecordSampleAsync(registration.State, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NormalizePositive(_options.Value.HeartbeatInterval, TimeSpan.FromSeconds(15));
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

            var refresh = await RegisterInstanceAsync(BuildLocalRequest(isRefresh: true), stoppingToken)
                .ConfigureAwait(false);
            if (!refresh.IsAccepted)
            {
                LicenseCapacityMeterLog.RefreshRefused(_logger, refresh.Reason);
            }

            await RecordSampleAsync(refresh.State, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> UpsertHeartbeatAsync(
        LicenseCapacityRegistrationRequest request,
        LicenseCapacityState state,
        bool enforce,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var instance = ToInstance(request, now);

        if (!UseRedis)
        {
            UpsertLocalInstance(instance);
            return true;
        }

        try
        {
            var database = _redis!.GetDatabase();
            var joiningMilliUnits = ToMilliUnits(instance.CountsTowardBand ? instance.ServingUnits : 0m);
            var ceiling = state.State == LicenseCapacityBandState.GraceExpired
                ? state.Terms?.MaxSustainedServingUnits ?? 0m
                : state.BurstCeilingServingUnits ?? 0m;
            var ceilingMilliUnits = ToMilliUnits(ceiling);

            var result = (RedisResult[]?)await database.ScriptEvaluateAsync(
                RegistrationScript,
                new RedisKey[] { InstanceSetKey, InstanceKey(instance.InstanceId) },
                new RedisValue[]
                {
                    joiningMilliUnits.ToString(CultureInfo.InvariantCulture),
                    enforce ? "1" : "0",
                    ceilingMilliUnits.ToString(CultureInfo.InvariantCulture),
                    FormatInstancePayload(instance),
                    HeartbeatTtlMilliseconds.ToString(CultureInfo.InvariantCulture)
                },
                CommandFlags.DemandMaster).ConfigureAwait(false);

            if (result is null || result.Length == 0)
            {
                // An absent/empty reply (e.g. NOSCRIPT, partial-cluster, or a replica
                // read) is not a capacity decision. Fail open exactly like a thrown
                // Redis error rather than surfacing a spurious band-ceiling refusal:
                // mark a metering gap and accept locally. A genuine refusal can only
                // come from a non-empty reply that explicitly reports the ceiling was
                // exceeded (handled below).
                MarkMeteringGap(new RedisException("Registration script returned no result; failing open."));
                UpsertLocalInstance(instance);
                return true;
            }

            _meteringGap = false;
            return (int)result[0] == 1;
        }
        catch (Exception ex) when (IsRedisException(ex))
        {
            MarkMeteringGap(ex);
            UpsertLocalInstance(instance);
            return true;
        }
    }

    private async Task<LicenseCapacityState> BuildStateAsync(
        LicenseCapacityTerms? terms,
        string? excludeInstanceId,
        bool updateGrace,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var localRequest = BuildLocalRequest(isRefresh: _registeredSelf);
        var localExcluded = LicenseCapacityCalculator.IsExcludedRole(localRequest.DeploymentRole);
        MeterRead read;

        if (UseRedis)
        {
            try
            {
                read = await ReadRedisMeterAsync(now, excludeInstanceId).ConfigureAwait(false);
                _meteringGap = false;
            }
            catch (Exception ex) when (IsRedisException(ex))
            {
                MarkMeteringGap(ex);
                read = ReadLocalMeter(now, excludeInstanceId) with { MeteringGap = true };
            }
        }
        else
        {
            read = ReadLocalMeter(now, excludeInstanceId);
        }

        var surge = read.Surge ?? new LocalSurgeState(false, null, null, 0m);
        var p95 = LicenseCapacityCalculator.ComputeP95(read.Samples);
        if (p95 == 0m && !surge.IsActive && read.CurrentServingUnits > 0m)
        {
            p95 = read.CurrentServingUnits;
        }

        var graceStartedAt = updateGrace
            ? await UpdateGraceAsync(terms, p95, surge.IsActive, read.MeteringGap, now).ConfigureAwait(false)
            : read.GraceStartedAt;

        var state = LicenseCapacityCalculator.DetermineBandState(
            terms,
            read.CurrentServingUnits,
            p95,
            graceStartedAt,
            surge.IsActive,
            read.MeteringGap,
            localExcluded,
            now);

        decimal? burstCeiling = terms is null
            ? null
            : LicenseCapacityCalculator.GetBurstCeiling(terms);
        var graceExpiresAt = graceStartedAt?.AddDays(LicenseCapacityCalculator.GracePeriodDays);
        var warning80 = terms is not null && p95 >= terms.MaxSustainedServingUnits * 0.8m;
        var warning100 = terms is not null && p95 > terms.MaxSustainedServingUnits;
        var productionCount = read.Instances.Count(instance => instance.CountsTowardBand);
        var excludedCount = read.Instances.Count - productionCount;

        return new LicenseCapacityState
        {
            State = state,
            Terms = terms,
            CurrentServingUnits = read.CurrentServingUnits,
            P95ServingUnits = p95,
            BurstCeilingServingUnits = burstCeiling,
            BurstMultiplier = LicenseCapacityCalculator.BurstMultiplier,
            GracePeriodDays = LicenseCapacityCalculator.GracePeriodDays,
            GraceStartedAt = graceStartedAt,
            GraceExpiresAt = graceExpiresAt,
            RegistrationEnforced = terms is not null && state != LicenseCapacityBandState.MeteringGap,
            RedisConfigured = _redis is not null,
            RedisCoordinated = UseRedis && !read.MeteringGap,
            MeteringGap = read.MeteringGap,
            LocalDeploymentRole = localRequest.DeploymentRole,
            LocalTopology = localRequest.Topology,
            LocalServingUnits = localRequest.ServingUnits,
            LocalRoleExcluded = localExcluded,
            LiveInstanceCount = read.Instances.Count,
            ProductionInstanceCount = productionCount,
            ExcludedInstanceCount = excludedCount,
            Surge = BuildSurgeState(surge, terms, now),
            Warning80Percent = warning80,
            Warning100Percent = warning100,
            Messages = BuildMessages(state, read.MeteringGap)
        };
    }

    private async Task<DateTimeOffset?> UpdateGraceAsync(
        LicenseCapacityTerms? terms,
        decimal p95ServingUnits,
        bool surgeActive,
        bool meteringGap,
        DateTimeOffset now)
    {
        if (terms is null || surgeActive || meteringGap)
        {
            return UseRedis
                ? await ReadRedisGraceAsync().ConfigureAwait(false)
                : _localGraceStartedAt;
        }

        if (p95ServingUnits > terms.MaxSustainedServingUnits)
        {
            var existing = UseRedis
                ? await ReadRedisGraceAsync().ConfigureAwait(false)
                : _localGraceStartedAt;
            if (existing.HasValue)
            {
                return existing;
            }

            if (UseRedis)
            {
                try
                {
                    await _redis!.GetDatabase().StringSetAsync(
                        GraceKey,
                        now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                        flags: CommandFlags.DemandMaster).ConfigureAwait(false);
                    _meteringGap = false;
                }
                catch (Exception ex) when (IsRedisException(ex))
                {
                    MarkMeteringGap(ex);
                    _localGraceStartedAt = now;
                }
            }
            else
            {
                _localGraceStartedAt = now;
            }

            return now;
        }

        if (UseRedis)
        {
            try
            {
                await _redis!.GetDatabase().KeyDeleteAsync(GraceKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                _meteringGap = false;
            }
            catch (Exception ex) when (IsRedisException(ex))
            {
                MarkMeteringGap(ex);
                _localGraceStartedAt = null;
            }
        }
        else
        {
            _localGraceStartedAt = null;
        }

        return null;
    }

    private async Task RecordSampleAsync(LicenseCapacityState state, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var sample = new LicenseCapacitySample
        {
            Timestamp = now,
            ServingUnits = state.CurrentServingUnits,
            IsSurge = state.Surge.IsActive
        };

        if (!UseRedis)
        {
            AddLocalSample(sample);
            return;
        }

        try
        {
            var database = _redis!.GetDatabase();
            var lockAcquired = await database.StringSetAsync(
                SampleLockKey,
                LocalInstanceId,
                expiry: NormalizePositive(_options.Value.SampleInterval, TimeSpan.FromMinutes(5)),
                when: When.NotExists,
                flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            if (!lockAcquired)
            {
                return;
            }

            await database.ListRightPushAsync(
                SampleKey,
                FormatSamplePayload(sample),
                flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.ListTrimAsync(SampleKey, -10_000, -1, CommandFlags.DemandMaster).ConfigureAwait(false);
            _meteringGap = false;
        }
        catch (Exception ex) when (IsRedisException(ex))
        {
            MarkMeteringGap(ex);
            AddLocalSample(sample);
        }

        _ = cancellationToken;
    }

    private async Task<MeterRead> ReadRedisMeterAsync(DateTimeOffset now, string? excludeInstanceId)
    {
        var database = _redis!.GetDatabase();
        var members = await database.SetMembersAsync(InstanceSetKey).ConfigureAwait(false);
        var instances = new List<LicenseCapacityInstance>(members.Length);

        foreach (var member in members)
        {
            var key = (RedisKey)member.ToString();
            var payload = await database.StringGetAsync(key).ConfigureAwait(false);
            if (!payload.HasValue)
            {
                await database.SetRemoveAsync(InstanceSetKey, member, CommandFlags.DemandMaster).ConfigureAwait(false);
                continue;
            }

            if (TryParseInstancePayload(key, payload.ToString(), out var instance) &&
                !string.Equals(instance.InstanceId, excludeInstanceId, StringComparison.Ordinal))
            {
                instances.Add(instance);
            }
        }

        var samples = await ReadRedisSamplesAsync(database, now).ConfigureAwait(false);
        var surge = await ReadRedisSurgeAsync(database, now).ConfigureAwait(false);
        var grace = await ReadRedisGraceAsync().ConfigureAwait(false);
        var currentUnits = instances
            .Where(instance => instance.CountsTowardBand)
            .Sum(instance => instance.ServingUnits);

        return new MeterRead(instances, samples, surge, grace, currentUnits, MeteringGap: false);
    }

    private MeterRead ReadLocalMeter(DateTimeOffset now, string? excludeInstanceId)
    {
        lock (_sync)
        {
            var windowStart = now - NormalizePositive(_options.Value.SampleWindow, TimeSpan.FromDays(30));
            _localSamples.RemoveAll(sample => sample.Timestamp < windowStart);
            var instances = _localInstances.Values
                .Where(instance => !string.Equals(instance.InstanceId, excludeInstanceId, StringComparison.Ordinal))
                .ToList();
            var samples = _localSamples.ToList();
            var currentUnits = instances
                .Where(instance => instance.CountsTowardBand)
                .Sum(instance => instance.ServingUnits);
            return new MeterRead(instances, samples, _localSurge, _localGraceStartedAt, currentUnits, _meteringGap);
        }
    }

    private async Task<IReadOnlyList<LicenseCapacitySample>> ReadRedisSamplesAsync(IDatabase database, DateTimeOffset now)
    {
        var windowStart = now - NormalizePositive(_options.Value.SampleWindow, TimeSpan.FromDays(30));
        var values = await database.ListRangeAsync(SampleKey).ConfigureAwait(false);
        var samples = new List<LicenseCapacitySample>(values.Length);
        foreach (var value in values)
        {
            if (TryParseSamplePayload(value.ToString(), out var sample) && sample.Timestamp >= windowStart)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private async Task<LocalSurgeState?> ReadRedisSurgeAsync(IDatabase database, DateTimeOffset now)
    {
        var payload = await database.StringGetAsync(SurgeKey).ConfigureAwait(false);
        var usedDays = await ReadRedisSurgeUsedDaysAsync(database, now).ConfigureAwait(false);
        if (!payload.HasValue || !TryParseSurgePayload(payload.ToString(), out var activatedAt, out var reason))
        {
            return usedDays == 0m
                ? null
                : new LocalSurgeState(false, null, null, usedDays);
        }

        return new LocalSurgeState(true, activatedAt, reason, usedDays);
    }

    private async Task<decimal> ReadRedisSurgeUsedDaysAsync(IDatabase database, DateTimeOffset now)
    {
        var value = await database.StringGetAsync(SurgeUsageKey(now)).ConfigureAwait(false);
        return value.HasValue &&
            decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var used)
            ? used
            : 0m;
    }

    private async Task<DateTimeOffset?> ReadRedisGraceAsync()
    {
        if (!UseRedis)
        {
            return _localGraceStartedAt;
        }

        try
        {
            var value = await _redis!.GetDatabase().StringGetAsync(GraceKey).ConfigureAwait(false);
            return value.HasValue &&
                long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs)
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                : null;
        }
        catch (Exception ex) when (IsRedisException(ex))
        {
            MarkMeteringGap(ex);
            return _localGraceStartedAt;
        }
    }

    private static LicenseSurgeModeState BuildSurgeState(
        LocalSurgeState? local,
        LicenseCapacityTerms? terms,
        DateTimeOffset now)
    {
        var allowance = terms?.AnnualSurgeDays;
        var activeDays = local?.IsActive == true && local.ActivatedAt.HasValue
            ? Math.Max(0m, (decimal)(now - local.ActivatedAt.Value).TotalDays)
            : 0m;
        var used = (local?.UsedDaysThisYear ?? 0m) + activeDays;
        var remaining = allowance.HasValue
            ? Math.Max(0m, allowance.Value - used)
            : (decimal?)null;

        return new LicenseSurgeModeState
        {
            IsActive = local?.IsActive == true,
            ActivatedAt = local?.ActivatedAt,
            Reason = local?.Reason,
            AnnualAllowanceDays = allowance,
            UsedDaysThisYear = decimal.Round(used, 4),
            RemainingDaysThisYear = remaining.HasValue ? decimal.Round(remaining.Value, 4) : null
        };
    }

    private void SetLocalSurge(bool enabled, string? reason, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (enabled)
            {
                _localSurge = new LocalSurgeState(true, now, reason, _localSurgeUsedDays);
                return;
            }

            if (_localSurge?.IsActive == true && _localSurge.ActivatedAt.HasValue)
            {
                _localSurgeUsedDays += Math.Max(0m, (decimal)(now - _localSurge.ActivatedAt.Value).TotalDays);
            }

            _localSurge = _localSurgeUsedDays == 0m
                ? null
                : new LocalSurgeState(false, null, null, _localSurgeUsedDays);
        }
    }

    private LicenseCapacityRegistrationRequest BuildLocalRequest(bool isRefresh)
        => new()
        {
            InstanceId = LocalInstanceId,
            ServingUnits = LicenseCapacityCalculator.ComputeServingUnits(
                _options.Value.ServingUnits,
                _options.Value.Vcpu,
                _options.Value.MemoryGiB,
                _options.Value.ServerlessConcurrentExecutions),
            DeploymentRole = ParseEnum(_options.Value.DeploymentRole, LicenseDeploymentRole.Production),
            Topology = ParseEnum(_options.Value.Topology, LicenseServingTopology.SingleNode),
            IsRefresh = isRefresh
        };

    private string LocalInstanceId
        => string.IsNullOrWhiteSpace(_options.Value.InstanceId)
            ? $"{Environment.MachineName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}"
            : _options.Value.InstanceId;

    private static LicenseCapacityRegistrationRequest NormalizeRequest(LicenseCapacityRegistrationRequest request)
        => new()
        {
            InstanceId = request.InstanceId.Trim(),
            ServingUnits = request.ServingUnits > 0m ? request.ServingUnits : 1m,
            DeploymentRole = request.DeploymentRole,
            Topology = request.Topology,
            IsRefresh = request.IsRefresh
        };

    private static LicenseCapacityInstance ToInstance(LicenseCapacityRegistrationRequest request, DateTimeOffset now)
        => new()
        {
            InstanceId = request.InstanceId,
            ServingUnits = request.ServingUnits,
            DeploymentRole = request.DeploymentRole,
            Topology = request.Topology,
            LastHeartbeatAt = now,
            CountsTowardBand = !LicenseCapacityCalculator.IsExcludedRole(request.DeploymentRole)
        };

    private void UpsertLocalInstance(LicenseCapacityInstance instance)
    {
        lock (_sync)
        {
            _localInstances[instance.InstanceId] = instance;
        }
    }

    private void AddLocalSample(LicenseCapacitySample sample)
    {
        lock (_sync)
        {
            var windowStart = sample.Timestamp - NormalizePositive(_options.Value.SampleWindow, TimeSpan.FromDays(30));
            _localSamples.RemoveAll(existing => existing.Timestamp < windowStart);
            _localSamples.Add(sample);
        }
    }

    private static IReadOnlyList<string> BuildMessages(LicenseCapacityBandState state, bool meteringGap)
    {
        if (meteringGap)
        {
            return ["Capacity enforcement is suspended because Redis coordination is unavailable; serving remains fail-open."];
        }

        return state switch
        {
            LicenseCapacityBandState.NotConfigured => ["No capacity band is encoded in the active license."],
            LicenseCapacityBandState.Excluded => ["This deployment role is excluded from production capacity accounting."],
            LicenseCapacityBandState.ApproachingBand => ["P95 serving capacity is at or above 80 percent of the licensed band."],
            LicenseCapacityBandState.Burst => ["Live serving capacity is using free burst headroom."],
            LicenseCapacityBandState.Grace => ["P95 serving capacity is above the licensed band; the sustained-growth grace clock is active."],
            LicenseCapacityBandState.GraceExpired => ["The sustained-growth grace period has expired; new production registrations above the base band are refused."],
            LicenseCapacityBandState.Surge => ["Declared surge mode is active; registrations are allowed and p95 samples are excluded."],
            _ => []
        };
    }

    private bool UseRedis => _redis?.IsConnected == true;

    private string KeyPrefix => _options.Value.RedisKeyPrefix.TrimEnd(':');

    private RedisKey InstanceSetKey => $"{KeyPrefix}:instances";

    private RedisKey InstanceKey(string instanceId)
        => $"{KeyPrefix}:instance:{Convert.ToHexString(Encoding.UTF8.GetBytes(instanceId))}";

    private RedisKey SampleKey => $"{KeyPrefix}:samples";

    private RedisKey SampleLockKey => $"{KeyPrefix}:sample-lock";

    private RedisKey SurgeKey => $"{KeyPrefix}:surge";

    private RedisKey GraceKey => $"{KeyPrefix}:grace-started";

    private RedisKey SurgeUsageKey(DateTimeOffset now) => $"{KeyPrefix}:surge-used-days:{now.Year.ToString(CultureInfo.InvariantCulture)}";

    private long HeartbeatTtlMilliseconds
        => (long)NormalizePositive(_options.Value.HeartbeatTtl, TimeSpan.FromSeconds(60)).TotalMilliseconds;

    private static long ToMilliUnits(decimal servingUnits)
        => (long)Math.Ceiling(servingUnits * 1000m);

    private static decimal FromMilliUnits(long milliUnits)
        => milliUnits / 1000m;

    private static string FormatInstancePayload(LicenseCapacityInstance instance)
        => string.Join(
            '|',
            ToMilliUnits(instance.ServingUnits).ToString(CultureInfo.InvariantCulture),
            instance.DeploymentRole.ToString(),
            instance.Topology.ToString(),
            instance.LastHeartbeatAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(Encoding.UTF8.GetBytes(instance.InstanceId)));

    private static bool TryParseInstancePayload(
        RedisKey key,
        string payload,
        out LicenseCapacityInstance instance)
    {
        instance = null!;
        var parts = payload.Split('|');
        if (parts.Length < 5 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliUnits) ||
            !Enum.TryParse(parts[1], ignoreCase: true, out LicenseDeploymentRole role) ||
            !Enum.TryParse(parts[2], ignoreCase: true, out LicenseServingTopology topology) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs))
        {
            return false;
        }

        var instanceId = TryDecodeHexString(parts[4], out var decoded)
            ? decoded
            : key.ToString();
        instance = new LicenseCapacityInstance
        {
            InstanceId = instanceId,
            ServingUnits = FromMilliUnits(milliUnits),
            DeploymentRole = role,
            Topology = topology,
            LastHeartbeatAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
            CountsTowardBand = !LicenseCapacityCalculator.IsExcludedRole(role)
        };
        return true;
    }

    private static string FormatSamplePayload(LicenseCapacitySample sample)
        => string.Join(
            '|',
            sample.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ToMilliUnits(sample.ServingUnits).ToString(CultureInfo.InvariantCulture),
            sample.IsSurge ? "1" : "0");

    private static bool TryParseSamplePayload(string payload, out LicenseCapacitySample sample)
    {
        sample = null!;
        var parts = payload.Split('|');
        if (parts.Length < 3 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliUnits))
        {
            return false;
        }

        sample = new LicenseCapacitySample
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
            ServingUnits = FromMilliUnits(milliUnits),
            IsSurge = parts[2] == "1"
        };
        return true;
    }

    private static string FormatSurgePayload(DateTimeOffset activatedAt, string? reason)
        => string.Join(
            '|',
            activatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(Encoding.UTF8.GetBytes(reason ?? string.Empty)));

    private static bool TryParseSurgePayload(
        string payload,
        out DateTimeOffset activatedAt,
        out string? reason)
    {
        activatedAt = default;
        reason = null;
        var parts = payload.Split('|', count: 2);
        if (parts.Length == 0 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs))
        {
            return false;
        }

        activatedAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (parts.Length > 1 && TryDecodeHexString(parts[1], out var decoded))
        {
            reason = decoded;
        }

        return true;
    }

    private static bool TryDecodeHexString(string value, out string decoded)
    {
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromHexString(value));
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static TimeSpan NormalizePositive(TimeSpan value, TimeSpan fallback)
        => value > TimeSpan.Zero ? value : fallback;

    private static bool IsRedisException(Exception ex)
        => ex is RedisException or RedisConnectionException or RedisTimeoutException or RedisServerException;

    private void MarkMeteringGap(Exception exception)
    {
        if (!_meteringGap)
        {
            LicenseCapacityMeterLog.RedisMeteringGap(_logger, exception.GetType().Name);
        }

        _meteringGap = true;
    }

    private sealed record MeterRead(
        IReadOnlyList<LicenseCapacityInstance> Instances,
        IReadOnlyList<LicenseCapacitySample> Samples,
        LocalSurgeState? Surge,
        DateTimeOffset? GraceStartedAt,
        decimal CurrentServingUnits,
        bool MeteringGap);

    private sealed record LocalSurgeState(
        bool IsActive,
        DateTimeOffset? ActivatedAt,
        string? Reason,
        decimal UsedDaysThisYear);
}
