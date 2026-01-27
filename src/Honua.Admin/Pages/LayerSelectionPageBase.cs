// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin.Models;
using Honua.Admin.Services;
using Microsoft.AspNetCore.Components;

namespace Honua.Admin.Pages;

public abstract class LayerSelectionPageBase : ComponentBase
{
    [Inject] protected ISecureConnectionsClient ConnectionsClient { get; set; } = default!;
    [Inject] protected ILayerPublishingClient LayerPublishingClient { get; set; } = default!;

    protected List<SecureConnectionSummary> Connections { get; } = new();
    protected List<PublishedLayerSummary> PublishedLayers { get; } = new();

    protected Guid? SelectedConnectionId { get; private set; }
    protected int? SelectedLayerId { get; private set; }
    protected PublishedLayerSummary? SelectedLayer { get; private set; }

    protected bool IsLoadingConnections { get; private set; }
    protected bool IsLoadingLayers { get; private set; }
    protected string? ErrorMessage { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await LoadConnectionsAsync();
        if (SelectedConnectionId.HasValue)
        {
            await LoadLayersAsync();
            await EnsureSelectedLayerAsync();
        }

        await OnAfterSelectionInitializedAsync();
    }

    protected virtual Task OnAfterSelectionInitializedAsync() => Task.CompletedTask;

    protected async Task HandleConnectionChanged(Guid? connectionId)
    {
        SelectedConnectionId = connectionId;
        SelectedLayerId = null;
        SelectedLayer = null;

        await OnConnectionChangedAsync();
        await RefreshAsync();
    }

    protected virtual Task OnConnectionChangedAsync() => Task.CompletedTask;

    protected async Task HandleLayerChanged(int? layerId)
    {
        await UpdateSelectedLayerAsync(layerId, forceReload: true);
    }

    protected async Task RefreshAsync()
    {
        if (!SelectedConnectionId.HasValue)
        {
            return;
        }

        await LoadLayersAsync();
        await EnsureSelectedLayerAsync();
    }

    protected async Task LoadConnectionsAsync()
    {
        IsLoadingConnections = true;
        ErrorMessage = null;
        StateHasChanged();

        var result = await ConnectionsClient.GetConnectionsAsync();
        IsLoadingConnections = false;

        if (!result.Success)
        {
            ErrorMessage = result.Message ?? "Failed to load connections.";
            return;
        }

        Connections.Clear();
        Connections.AddRange(result.Data ?? Array.Empty<SecureConnectionSummary>());

        if (Connections.Count > 0 && !SelectedConnectionId.HasValue)
        {
            SelectedConnectionId = Connections[0].ConnectionId;
        }
    }

    protected async Task LoadLayersAsync()
    {
        IsLoadingLayers = true;
        ErrorMessage = null;
        StateHasChanged();

        if (!SelectedConnectionId.HasValue)
        {
            IsLoadingLayers = false;
            return;
        }

        var result = await LayerPublishingClient.GetPublishedLayersAsync(SelectedConnectionId.Value);
        IsLoadingLayers = false;

        if (!result.Success)
        {
            ErrorMessage = result.Message ?? "Failed to load layers.";
            PublishedLayers.Clear();
            return;
        }

        PublishedLayers.Clear();
        PublishedLayers.AddRange(result.Data ?? Array.Empty<PublishedLayerSummary>());
    }

    protected async Task UpdateSelectedLayerAsync(int? layerId, bool forceReload)
    {
        var previousLayerId = SelectedLayerId;
        SelectedLayerId = layerId;
        SelectedLayer = layerId.HasValue
            ? PublishedLayers.FirstOrDefault(layer => layer.LayerId == layerId.Value)
            : null;

        if (forceReload || previousLayerId != SelectedLayerId)
        {
            await OnLayerChangedAsync();
        }
    }

    private async Task EnsureSelectedLayerAsync()
    {
        var previousLayerId = SelectedLayerId;

        if (SelectedLayerId.HasValue)
        {
            var match = PublishedLayers.FirstOrDefault(layer => layer.LayerId == SelectedLayerId.Value);
            if (match != null)
            {
                SelectedLayer = match;
                return;
            }
        }

        var candidate = PublishedLayers.FirstOrDefault(layer => layer.Enabled) ?? PublishedLayers.FirstOrDefault();

        SelectedLayerId = candidate?.LayerId;
        SelectedLayer = candidate;

        if (previousLayerId != SelectedLayerId)
        {
            await OnLayerChangedAsync();
        }
    }

    protected virtual Task OnLayerChangedAsync() => Task.CompletedTask;
}
