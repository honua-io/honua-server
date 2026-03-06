// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

namespace HonuaFieldApp;

/// <summary>
/// Application shell providing navigation structure for the Honua Field App.
/// </summary>
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register routes for navigation
        Routing.RegisterRoute("mapdetails", typeof(Views.MapDetailsPage));
        Routing.RegisterRoute("featuredetails", typeof(Views.FeatureDetailsPage));
        Routing.RegisterRoute("photoviewer", typeof(Views.PhotoViewerPage));
        Routing.RegisterRoute("formeditor", typeof(Views.FormEditorPage));
    }
}