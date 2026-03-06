// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

using HonuaFieldApp.Views;

namespace HonuaFieldApp;

/// <summary>
/// Main application class for Honua Field Data Collection App.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        MainPage = new AppShell();
    }
}