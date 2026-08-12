using LeverageTradingStrategies.Api.Jobs;
using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Quotes;
using LeverageTradingStrategies.Infrastructure.State;
using Quartz;
using SchwabApiCS;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppSettingsOptions>(builder.Configuration.GetSection("AppSettings"));

// --- Serilog: console + rolling file. (No SQLite/Telegram sinks yet, unlike
// MarketMatrixPreparer's Program.cs -- add those back if/when this needs the same
// alerting surface; kept minimal for v1.) ---
builder.Host.UseSerilog((context, services, loggerConfig) =>
{
    loggerConfig
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: builder.Configuration["ConnectionStrings:LogFilePath"] ?? "logs/leverage-trading-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
});

builder.Services.AddOpenApi();
builder.Services.AddControllers();

// --- Schwab client + broker/quote-provider selection ---
var schwabTokenPath = builder.Configuration["AppSettings:Trading:SchwabTokenPath"];
if (!string.IsNullOrWhiteSpace(schwabTokenPath))
{
    builder.Services.AddScoped(_ => new SchwabApi(schwabTokenPath));
}

var useSimulatedBroker = builder.Configuration.GetValue<bool>("AppSettings:Trading:UseSimulatedBroker");
if (useSimulatedBroker)
{
    // Single shared instance so IBroker and IQuoteProvider see the same in-memory state.
    builder.Services.AddSingleton<SimulatedBroker>();
    builder.Services.AddSingleton<IBroker>(sp => sp.GetRequiredService<SimulatedBroker>());
    builder.Services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<SimulatedBroker>());
}
else
{
    builder.Services.AddScoped<IBroker, SchwabBroker>();
    builder.Services.AddScoped<IQuoteProvider, SchwabQuoteProvider>();
}

// --- TQQQ weekly strategy ---
builder.Services.AddSingleton<ITqqqWeeklyStateStore, JsonFileTqqqWeeklyStateStore>();
builder.Services.AddScoped<ITqqqWeeklyStrategyService, TqqqWeeklyStrategyService>();

// --- Quartz ---
builder.Services.AddQuartz(q =>
{
    var jobKey = new JobKey("TqqqWeeklyLiveTradingJob");
    q.AddJob<TqqqWeeklyLiveTradingJob>(opts => opts.WithIdentity(jobKey));

    var cron = builder.Configuration["AppSettings:TqqqWeekly:CronSchedule"] ?? "0 */5 9-16 ? * MON-FRI";
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("TqqqWeeklyLiveTradingJob-trigger")
        .WithCronSchedule(cron));
});
builder.Services.AddQuartzHostedService(opts => opts.WaitForJobsToComplete = true);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSerilogRequestLogging();
app.MapControllers();

app.Run();
