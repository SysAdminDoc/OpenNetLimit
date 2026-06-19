using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service;
using OpenNetLimit.Service.IPC;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IFlowTracker, FlowTracker>();
builder.Services.AddSingleton<IRateLimiter, ProcessRateLimiter>();
builder.Services.AddSingleton<ITrafficMonitor, TrafficMonitor>();
builder.Services.AddSingleton<IRuleEngine, RuleEngine>();
builder.Services.AddSingleton<IPacketInterceptor, WinDivertInterceptor>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<EngineWorker>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpenNetLimit";
});

var host = builder.Build();
host.Run();
