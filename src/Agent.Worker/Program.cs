using Agent.Core;
using Agent.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<FileBusOptions>(builder.Configuration.GetSection("FileBus"));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<KustoOptions>(builder.Configuration.GetSection("Kusto"));

builder.Services.AddSingleton(sp =>
    new FileBus(sp.GetRequiredService<IOptions<FileBusOptions>>().Value));

builder.Services.AddSingleton<ITaskHandler, EchoTaskHandler>();
builder.Services.AddSingleton<ITaskHandler, KustoTaskHandler>();
builder.Services.AddHostedService<WatcherWorker>();

var host = builder.Build();
host.Run();
