using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using WindowsFormsLifetime;
using OSCLeash.App;
using Serilog;
using OSCLeash.App.Services;
using OSCLeash.App.Services.Leash;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Serilog.Formatting.Compact;

ApplicationConfiguration.Initialize();

using var log = new LoggerConfiguration()
#if DEBUG
    .MinimumLevel.Debug()
    .WriteTo.Debug(formatter: new RenderedCompactJsonFormatter(), restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
#else
    .MinimumLevel.Information()
#endif
    .MinimumLevel.Override("Microsoft.AspNetCore.Components", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.File(new RenderedCompactJsonFormatter(), "log-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();
Log.Logger = log;

var builder = Host.CreateApplicationBuilder(args);
builder.UseWindowsFormsLifetime<MainWindow>();

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddHostedService(x => x.GetRequiredService<SettingsService>());
builder.Services.AddSingleton<ISettingsService>(x => x.GetRequiredService<SettingsService>());
builder.Services.AddSingleton<StatusService>();

builder.Services.AddTransient<IValidator<Settings>, SettingsValidator>();

builder.Services.AddSingleton<OSCServer>();
builder.Services.AddHostedService(x => x.GetRequiredService<OSCServer>());

builder.Services.AddWindowsFormsBlazorWebView();
builder.Services.AddFluentUIComponents();
builder.Services.AddHttpClient();

builder.Services.AddLogging(builder => {
    builder.ClearProviders();
    builder.AddSerilog(dispose: true);
});

#if DEBUG
builder.Services.AddBlazorWebViewDeveloperTools();
#endif

using var app = builder.Build();
app.Run();