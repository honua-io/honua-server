// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// TODO: Add services

var app = builder.Build();

// Configure health endpoints
app.MapHealthEndpoints();

app.Run();

// Make Program accessible to WebApplicationFactory
public partial class Program { }
