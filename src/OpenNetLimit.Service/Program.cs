using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service;
using OpenNetLimit.Service.API;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.Geo;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Plugins;
using OpenNetLimit.Service.Security;

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "OpenNetLimit");
var logDir = Path.Combine(dataDir, "logs");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});

builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "OpenNetLimit";
    settings.LogName = "Application";
});

try { Directory.CreateDirectory(logDir); } catch { }

builder.Services.AddSingleton<IFlowTracker, FlowTracker>();
builder.Services.AddSingleton<IRateLimiter, ProcessRateLimiter>();
builder.Services.AddSingleton<ITrafficMonitor, TrafficMonitor>();
builder.Services.AddSingleton<IRuleEngine, RuleEngine>();
builder.Services.AddSingleton<IPacketInterceptor, WinDivertInterceptor>();
builder.Services.AddSingleton<BandwidthAlertTracker>();
builder.Services.AddSingleton(RestApiOptions.FromEnvironment());
builder.Services.AddSingleton(VirusTotalOptions.FromEnvironment());
builder.Services.AddSingleton(GeoIpOptions.FromEnvironment());
builder.Services.AddSingleton(PluginOptions.FromEnvironment());
builder.Services.AddSingleton<ControlPlaneState>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddSingleton<RestApiRouter>();
builder.Services.AddSingleton<IProcessVerifier, VirusTotalVerifier>();
builder.Services.AddSingleton<IGeoIpResolver, FreeIpApiGeoIpResolver>();
builder.Services.AddSingleton<PluginManager>();
builder.Services.AddHostedService<EngineWorker>();
builder.Services.AddHostedService<RestApiServer>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpenNetLimit";
});

var host = builder.Build();
host.Run();
