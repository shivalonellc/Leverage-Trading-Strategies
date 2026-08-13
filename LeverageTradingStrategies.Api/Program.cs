using LeverageTradingStrategies.Api.Jobs;
using LeverageTradingStrategies.Domain.Options;
using LeverageTradingStrategies.Domain.Orders;
using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Options;
using LeverageTradingStrategies.Infrastructure.Quotes;
using LeverageTradingStrategies.Infrastructure.State;
using Quartz;
using SchwabApiCS;
using Serilog;
using Tradier.Client;

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

// Both concrete broker implementations are ALWAYS registered, regardless of
// UseSimulatedBroker below -- BrokerTestController deliberately targets either one
// per-request via its own "live" flag, independent of what the live trading job
// defaults to. Single shared SimulatedBroker instance so IBroker/IQuoteProvider (when
// resolved to it) and the test controller see the same in-memory state.
builder.Services.AddSingleton<SimulatedBroker>();
builder.Services.AddScoped<SchwabBroker>();
builder.Services.AddScoped<SchwabQuoteProvider>();

var useSimulatedBroker = builder.Configuration.GetValue<bool>("AppSettings:Trading:UseSimulatedBroker");
if (useSimulatedBroker)
{
    builder.Services.AddSingleton<IBroker>(sp => sp.GetRequiredService<SimulatedBroker>());
    builder.Services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<SimulatedBroker>());
}
else
{
    builder.Services.AddScoped<IBroker>(sp => sp.GetRequiredService<SchwabBroker>());
    builder.Services.AddScoped<IQuoteProvider>(sp => sp.GetRequiredService<SchwabQuoteProvider>());
}

// --- SQLite persistence (strategy instances, per-strategy state, order audit trail) ---
var sqliteConnectionString = builder.Configuration["ConnectionStrings:SqliteDb"] ?? "Data Source=leverage-trading.db";
builder.Services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(sqliteConnectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddScoped<IStrategyInstanceRepository, SqliteStrategyInstanceRepository>();
builder.Services.AddScoped<IStrategyOrderRepository, SqliteStrategyOrderRepository>();
builder.Services.AddScoped<IStrategyConfigRepository, SqliteStrategyConfigRepository>();
builder.Services.AddScoped<ITqqqWeeklyConfigProvider, TqqqWeeklyConfigProvider>();
// Scoped (not Singleton): depends on IBroker, which is Scoped when UseSimulatedBroker=false
// (SchwabBroker) — a Singleton here would be a captive-dependency error in that configuration.
builder.Services.AddScoped<IStrategyOrderExecutor, StrategyOrderExecutor>();

// --- TQQQ weekly strategy ---
builder.Services.AddSingleton<ITqqqWeeklyStateStore, SqliteTqqqWeeklyStateStore>();
builder.Services.AddScoped<ITqqqWeeklyStrategyService, TqqqWeeklyStrategyService>();

// --- Redis-backed distributed cache: short-TTL cache for Tradier chain/expiration lookups,
// shared by the vertical-spread builder UI, VerticalSpreadMarkingJob, and any concurrent
// request, so the same symbol+expiration isn't re-fetched from Tradier on every call. The
// underlying connection is established lazily (on first cache use, not at startup), and
// CachedTradierOptionsProvider wraps every Redis call in try/catch, so a Redis outage degrades
// to "no caching" rather than breaking the endpoint. ---
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration["ConnectionStrings:Redis"] ?? "localhost:6379";
    options.InstanceName = "lts:"; // key prefix -- avoids collisions if this Redis instance is shared with other apps
});

// --- Tradier (option chain/greeks data ONLY -- order execution stays on Schwab above) ---
builder.Services.AddScoped<TradierClient>(sp =>
    new TradierClient(
        builder.Configuration["AppSettings:Tradier:Token"],
        builder.Configuration["AppSettings:Tradier:AccountId"],
        builder.Configuration.GetValue<bool>("AppSettings:Tradier:UseSandbox")?false:true));
// TradierOptionsProvider is registered under its own concrete type (not the interface) so the
// cache decorator below can depend on "the real thing" without DI wiring the decorator to
// itself. ITradierOptionsProvider (what every controller/job actually injects) resolves to the
// decorator, which is transparent to callers -- same interface, same method signatures.
builder.Services.AddScoped<TradierOptionsProvider>();
builder.Services.AddScoped<ITradierOptionsProvider, CachedTradierOptionsProvider>();

// --- Vertical credit spread module ---
builder.Services.AddScoped<IVerticalSpreadRepository, SqliteVerticalSpreadRepository>();
builder.Services.AddSingleton<IVerticalSpreadPricingService, VerticalSpreadPricingService>(); // pure math, no state
builder.Services.AddScoped<IVerticalSpreadOrderExecutor, VerticalSpreadOrderExecutor>();

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

    var spreadJobKey = new JobKey("VerticalSpreadMarkingJob");
    q.AddJob<VerticalSpreadMarkingJob>(opts => opts.WithIdentity(spreadJobKey));

    var spreadCron = builder.Configuration["AppSettings:VerticalSpread:MarkingCronSchedule"] ?? "0 */5 9-16 ? * MON-FRI";
    q.AddTrigger(opts => opts
        .ForJob(spreadJobKey)
        .WithIdentity("VerticalSpreadMarkingJob-trigger")
        .WithCronSchedule(spreadCron));
});
builder.Services.AddQuartzHostedService(opts => opts.WaitForJobsToComplete = true);

var app = builder.Build();

// Idempotent — CREATE TABLE/INDEX IF NOT EXISTS, safe to run on every startup.
app.Services.GetRequiredService<DatabaseInitializer>().EnsureCreated();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSerilogRequestLogging();
app.UseStaticFiles(); // serves wwwroot/dashboard.html
app.MapControllers();

app.Run();
