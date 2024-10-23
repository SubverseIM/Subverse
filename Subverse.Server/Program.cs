using Subverse;
using Subverse.Server;

var builder = Host.CreateApplicationBuilder(args);

// Helpers
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();

// Mission-critical
builder.Services.AddSingleton<IPeerService, RoutedPeerService>();
builder.Services.AddHostedService<HubService>();

// Windows-specific
builder.Services.AddWindowsService(options => 
{
    options.ServiceName = "Subverse.NET Hub Service";
});

var host = builder.Build();
host.Run();
