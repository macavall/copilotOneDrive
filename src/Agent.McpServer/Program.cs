using Agent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Transport selection: "stdio" (default, launched locally by a Copilot client)
// or "http" (network endpoint that can be port-forwarded through a code tunnel
// so a remote client such as Copilot on a phone can reach it).
var transport = (Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio")
    .Trim()
    .ToLowerInvariant();

var root = Environment.GetEnvironmentVariable("COPILOTBUS_ROOT")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "OneDrive", "CopilotBus");

var bus = new FileBus(new FileBusOptions { RootDirectory = root });

if (transport == "http")
{
    var webBuilder = WebApplication.CreateBuilder(args);

    // Bind address for the HTTP transport; override with MCP_HTTP_URLS if needed.
    var urls = Environment.GetEnvironmentVariable("MCP_HTTP_URLS") ?? "http://127.0.0.1:5250";
    webBuilder.WebHost.UseUrls(urls);

    webBuilder.Services.AddSingleton(bus);
    webBuilder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = webBuilder.Build();
    app.MapMcp();
    await app.RunAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// MCP uses stdout for protocol traffic; all logs must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(bus);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
