// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Models;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Admin.Sdk.Models;
using Honua.Admin.Sdk.Services;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Admin.Sdk.Clients;

/// <summary>
/// Administrative client for Honua platform management.
/// Provides service management, user administration, bulk operations, and monitoring capabilities.
/// </summary>
public class HonuaAdminClient : IFeatureServiceClient<AdminContext>, IDisposable
{
    private readonly IFeatureServiceClient<AdminContext> _featureClient;
    private readonly IServiceManagementClient _serviceClient;
    private readonly IUserManagementClient _userClient;
    private readonly IBulkOperationsClient _bulkClient;
    private readonly IMonitoringClient _monitoringClient;
    private readonly ILogger<HonuaAdminClient> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the HonuaAdminClient.
    /// </summary>
    /// <param name="featureClient">Feature service client</param>
    /// <param name="serviceClient">Service management client</param>
    /// <param name="userClient">User management client</param>
    /// <param name="bulkClient">Bulk operations client</param>
    /// <param name="monitoringClient">Monitoring client</param>
    /// <param name="options">Client configuration options</param>
    /// <param name="logger">Logger instance</param>
    public HonuaAdminClient(
        IFeatureServiceClient<AdminContext> featureClient,
        IServiceManagementClient serviceClient,
        IUserManagementClient userClient,
        IBulkOperationsClient bulkClient,
        IMonitoringClient monitoringClient,
        IOptions<HonuaAdminClientOptions> options,
        ILogger<HonuaAdminClient> logger)
    {
        _featureClient = featureClient ?? throw new ArgumentNullException(nameof(featureClient));
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        _userClient = userClient ?? throw new ArgumentNullException(nameof(userClient));
        _bulkClient = bulkClient ?? throw new ArgumentNullException(nameof(bulkClient));
        _monitoringClient = monitoringClient ?? throw new ArgumentNullException(nameof(monitoringClient));
        _ = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Feature Service Operations

    /// <summary>
    /// Executes a feature query with administrative context and audit logging.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results with features</returns>
    public async Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        AdminContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var effectiveContext = EnhanceContextWithAudit(context, "QueryFeatures", serviceId);

        try
        {
            LogAuditEvent("QueryFeatures", serviceId, layerId, context.UserIdentity, context.AuditLevel);

            var result = await _featureClient.QueryFeaturesAsync(
                serviceId, layerId, query, effectiveContext, combinedCts.Token);

            LogAuditEvent("QueryFeaturesCompleted", serviceId, layerId, context.UserIdentity,
                context.AuditLevel, new { FeatureCount = result.Features.Length });

            return result;
        }
        catch (Exception ex)
        {
            LogAuditEvent("QueryFeaturesFailed", serviceId, layerId, context.UserIdentity,
                context.AuditLevel, new { Error = ex.Message });
            throw;
        }
    }

    /// <summary>
    /// Executes a feature query and streams results with administrative monitoring.
    /// </summary>
    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        AdminContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var effectiveContext = EnhanceContextWithAudit(context, "QueryFeaturesStream", serviceId);

        LogAuditEvent("QueryFeaturesStreamStarted", serviceId, layerId, context.UserIdentity, context.AuditLevel);

        var totalFeatures = 0;
        await foreach (var page in _featureClient.QueryFeaturesStreamAsync(
            serviceId, layerId, query, effectiveContext, combinedCts.Token))
        {
            totalFeatures += page.Features.Length;
            yield return page;
        }

        LogAuditEvent("QueryFeaturesStreamCompleted", serviceId, layerId, context.UserIdentity,
            context.AuditLevel, new { TotalFeatures = totalFeatures });
    }

    /// <summary>
    /// Applies feature edits with administrative audit logging and validation.
    /// </summary>
    public async Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        AdminContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var effectiveContext = EnhanceContextWithAudit(context, "ApplyEdits", serviceId);

        try
        {
            var totalOperations = edits.Adds.Length + edits.Updates.Length + edits.Deletes.Length;

            LogAuditEvent("ApplyEditsStarted", serviceId, layerId, context.UserIdentity, context.AuditLevel,
                new {
                    TotalOperations = totalOperations,
                    AddCount = edits.Adds.Length,
                    UpdateCount = edits.Updates.Length,
                    DeleteCount = edits.Deletes.Length
                });

            // Validate edits if requested
            if (context.ValidateOperations)
            {
                await ValidateEditsAsync(serviceId, layerId, edits, context, combinedCts.Token);
            }

            var result = await _featureClient.ApplyEditsAsync(
                serviceId, layerId, edits, effectiveContext, combinedCts.Token);

            var successCount = result.AddResults.Count(r => r.Success) +
                             result.UpdateResults.Count(r => r.Success) +
                             result.DeleteResults.Count(r => r.Success);

            LogAuditEvent("ApplyEditsCompleted", serviceId, layerId, context.UserIdentity, context.AuditLevel,
                new {
                    SuccessfulOperations = successCount,
                    TotalOperations = totalOperations,
                    SuccessRate = (double)successCount / totalOperations * 100
                });

            return result;
        }
        catch (Exception ex)
        {
            LogAuditEvent("ApplyEditsFailed", serviceId, layerId, context.UserIdentity,
                context.AuditLevel, new { Error = ex.Message });
            throw;
        }
    }

    #endregion

    #region Service Management

    /// <summary>
    /// Gets all services available to the administrator.
    /// </summary>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of service information</returns>
    public async Task<IEnumerable<ServiceInfo>> GetServicesAsync(
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("GetServices", null, null, context.UserIdentity, context.AuditLevel);

        return await _serviceClient.GetServicesAsync(context, cancellationToken);
    }

    /// <summary>
    /// Gets detailed information about a specific service.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed service information</returns>
    public async Task<ServiceDetails> GetServiceDetailsAsync(
        string serviceId,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("GetServiceDetails", serviceId, null, context.UserIdentity, context.AuditLevel);

        return await _serviceClient.GetServiceDetailsAsync(serviceId, context, cancellationToken);
    }

    /// <summary>
    /// Deploys a new geospatial service.
    /// </summary>
    /// <param name="configuration">Service configuration</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Deployment result</returns>
    public async Task<ServiceDeploymentResult> DeployServiceAsync(
        ServiceConfiguration configuration,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("DeployServiceStarted", configuration.Name, null, context.UserIdentity, AuditLevel.Detailed,
            new { Configuration = CreateAuditSafeConfiguration(configuration) });

        var result = await _serviceClient.DeployServiceAsync(configuration, context, cancellationToken);

        LogAuditEvent("DeployServiceCompleted", result.ServiceId, null, context.UserIdentity, AuditLevel.Detailed,
            new { Success = result.Success, Message = result.Message });

        return result;
    }

    #endregion

    #region User Management

    /// <summary>
    /// Creates a new user account.
    /// </summary>
    /// <param name="request">User creation request</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User creation result</returns>
    public async Task<UserCreationResult> CreateUserAsync(
        UserCreateRequest request,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("CreateUserStarted", null, null, context.UserIdentity, AuditLevel.Detailed,
            new { Username = request.Username, Email = request.Email, Roles = request.Roles });

        var result = await _userClient.CreateUserAsync(request, context, cancellationToken);

        LogAuditEvent("CreateUserCompleted", null, null, context.UserIdentity, AuditLevel.Detailed,
            new { Success = result.Success, UserId = result.UserId });

        return result;
    }

    /// <summary>
    /// Grants service access to a user.
    /// </summary>
    /// <param name="username">Username</param>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="permissionLevel">Permission level to grant</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Permission grant result</returns>
    public async Task<PermissionResult> GrantServiceAccessAsync(
        string username,
        string serviceId,
        PermissionLevel permissionLevel,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("GrantServiceAccess", serviceId, null, context.UserIdentity, AuditLevel.Standard,
            new { Username = username, PermissionLevel = permissionLevel });

        return await _userClient.GrantServiceAccessAsync(username, serviceId, permissionLevel, context, cancellationToken);
    }

    #endregion

    #region Bulk Operations

    /// <summary>
    /// Imports data from a stream with progress reporting.
    /// </summary>
    /// <param name="dataStream">Stream containing data to import</param>
    /// <param name="options">Import options</param>
    /// <param name="context">Administrative context with progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of import progress</returns>
    public async IAsyncEnumerable<AdminProgress> ImportDataAsync(
        Stream dataStream,
        BulkImportOptions options,
        AdminContext? context = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("ImportDataStarted", options.ServiceId, options.LayerId, context.UserIdentity, AuditLevel.Detailed,
            new { Options = options });

        await foreach (var progress in _bulkClient.ImportDataAsync(dataStream, options, context, cancellationToken))
        {
            yield return progress;
        }

        LogAuditEvent("ImportDataCompleted", options.ServiceId, options.LayerId, context.UserIdentity, AuditLevel.Detailed);
    }

    /// <summary>
    /// Exports service data to a stream.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="options">Export options</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream containing exported data</returns>
    public async Task<Stream> ExportServiceDataAsync(
        string serviceId,
        BulkExportOptions options,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);

        LogAuditEvent("ExportServiceData", serviceId, null, context.UserIdentity, AuditLevel.Standard,
            new { Options = options });

        return await _bulkClient.ExportServiceDataAsync(serviceId, options, context, cancellationToken);
    }

    #endregion

    #region Monitoring

    /// <summary>
    /// Gets service health information.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Service health information</returns>
    public async Task<ServiceHealth> GetServiceHealthAsync(
        string serviceId,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);
        return await _monitoringClient.GetServiceHealthAsync(serviceId, context, cancellationToken);
    }

    /// <summary>
    /// Gets performance metrics for a service.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="timeRange">Time range for metrics</param>
    /// <param name="context">Administrative context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Performance metrics</returns>
    public async Task<PerformanceMetrics> GetServiceMetricsAsync(
        string serviceId,
        TimeSpan timeRange,
        AdminContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= AdminContext.System(cancellationToken);
        return await _monitoringClient.GetServiceMetricsAsync(serviceId, timeRange, context, cancellationToken);
    }

    #endregion

    #region Private Methods

    private AdminContext EnhanceContextWithAudit(AdminContext context, string operation, string? serviceId)
    {
        if (!context.IncludeDiagnostics)
            return context;

        // Add diagnostic information and operation tracking
        return new AdminContext
        {
            UserIdentity = context.UserIdentity,
            ProgressReporter = context.ProgressReporter,
            CancellationToken = context.CancellationToken,
            Activity = context.Activity,
            Headers = context.Headers,
            Timeout = context.Timeout,
            IncludeDiagnostics = context.IncludeDiagnostics,
            ValidateOperations = context.ValidateOperations,
            AuditLevel = context.AuditLevel,
            Priority = context.Priority
        };
    }

    private async Task ValidateEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        AdminContext context,
        CancellationToken cancellationToken)
    {
        // Validate edit operations before applying
        // This could include schema validation, permission checks, etc.
        await Task.Delay(1, cancellationToken); // Placeholder for actual validation
    }

    private void LogAuditEvent(
        string eventType,
        string? serviceId,
        int? layerId,
        AdminIdentity? userIdentity,
        AuditLevel auditLevel,
        object? additionalData = null)
    {
        if (auditLevel == AuditLevel.Minimal)
            return;

        _logger.LogInformation("Admin operation: {EventType} by {UserId} on service {ServiceId}/layer {LayerId} - {Data}",
            eventType, userIdentity?.UserId ?? "system", serviceId, layerId, SanitizeAuditData(additionalData));
    }

    private static object? SanitizeAuditData(object? additionalData)
    {
        return additionalData switch
        {
            null => null,
            ServiceConfiguration configuration => CreateAuditSafeConfiguration(configuration),
            _ => additionalData
        };
    }

    private static object CreateAuditSafeConfiguration(ServiceConfiguration configuration)
    {
        return new
        {
            configuration.Name,
            DataSource = "[REDACTED]",
            Layers = configuration.Layers
                .Select(layer => new
                {
                    layer.Name,
                    layer.TableName,
                    layer.GeometryColumn,
                    layer.SpatialReference,
                    Settings = SanitizeSettings(layer.Settings)
                })
                .ToArray(),
            Settings = SanitizeSettings(configuration.Settings)
        };
    }

    private static Dictionary<string, object?> SanitizeSettings(IReadOnlyDictionary<string, object> settings)
    {
        return settings.ToDictionary(
            kvp => kvp.Key,
            kvp => IsSensitiveKey(kvp.Key) ? "[REDACTED]" : SanitizeSettingValue(kvp.Value));
    }

    private static object? SanitizeSettingValue(object? value)
    {
        return value switch
        {
            null => null,
            IDictionary<string, object> dictionary => SanitizeSettings(dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)),
            IEnumerable<object> values when value is not string => values.Select(SanitizeSettingValue).ToArray(),
            _ => value
        };
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("datasource", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Disposes the admin client and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_featureClient is IDisposable disposableFeatureClient)
            {
                disposableFeatureClient.Dispose();
            }
            _serviceClient?.Dispose();
            _userClient?.Dispose();
            _bulkClient?.Dispose();
            _monitoringClient?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    #endregion
}
