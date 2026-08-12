using System.Globalization;
using LeverageTradingStrategies.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace LeverageTradingStrategies.Infrastructure.Configuration
{
    public class TqqqWeeklyConfigProvider : ITqqqWeeklyConfigProvider
    {
        // Key names for the StrategyConfig table -- kept as constants here (the only place
        // that reads/writes them) so there's exactly one source of truth for the string keys.
        private const string KeyBaseSizeFraction = "BaseSizeFraction";
        private const string KeyVolBoostFraction = "VolBoostFraction";
        private const string KeyVolLookbackDays = "VolLookbackDays";
        private const string KeyVolHistoryMaxReadings = "VolHistoryMaxReadings";
        private const string KeyVolHistoryMinReadings = "VolHistoryMinReadings";
        private const string KeyVolPercentileThreshold = "VolPercentileThreshold";
        private const string KeyTierHighMultiplier = "TierHighMultiplier";
        private const string KeyTierMidMultiplier = "TierMidMultiplier";
        private const string KeyTierLowMultiplier = "TierLowMultiplier";
        private const string KeyTierProfitHighThreshold = "TierProfitHighThreshold";
        private const string KeyAvgDownTrigger = "AvgDownTrigger";
        private const string KeyAvgDownFraction = "AvgDownFraction";
        private const string KeyMondayAvgDownTrigger = "MondayAvgDownTrigger";
        private const string KeyMondayAvgDownFraction = "MondayAvgDownFraction";
        private const string KeyCloseStopPct = "CloseStopPct";
        private const string KeyEntryDayCloseStopPct = "EntryDayCloseStopPct";
        private const string KeyForceCloseWeekly = "ForceCloseWeekly";
        private const string KeyForceCloseHourEt = "ForceCloseHourEt";

        /// <summary>Every valid StrategyConfig key for this strategy type — used by the
        /// controller to reject typos with a 400 instead of silently writing a key that's
        /// never read.</summary>
        public static readonly IReadOnlyList<string> KnownKeys = new[]
        {
            KeyBaseSizeFraction, KeyVolBoostFraction, KeyVolLookbackDays, KeyVolHistoryMaxReadings,
            KeyVolHistoryMinReadings, KeyVolPercentileThreshold, KeyTierHighMultiplier, KeyTierMidMultiplier,
            KeyTierLowMultiplier, KeyTierProfitHighThreshold, KeyAvgDownTrigger, KeyAvgDownFraction,
            KeyMondayAvgDownTrigger, KeyMondayAvgDownFraction, KeyCloseStopPct, KeyEntryDayCloseStopPct,
            KeyForceCloseWeekly, KeyForceCloseHourEt
        };

        private readonly IStrategyConfigRepository _configRepository;
        private readonly IOptions<AppSettingsOptions> _options;

        public TqqqWeeklyConfigProvider(IStrategyConfigRepository configRepository, IOptions<AppSettingsOptions> options)
        {
            _configRepository = configRepository;
            _options = options;
        }

        public async Task<TqqqWeeklyRuntimeConfig> GetAsync(int strategyInstanceId, CancellationToken ct = default)
        {
            var d = _options.Value.TqqqWeekly;
            var defaults = new Dictionary<string, string>
            {
                [KeyBaseSizeFraction] = ToStr(d.BaseSizeFraction),
                [KeyVolBoostFraction] = ToStr(d.VolBoostFraction),
                [KeyVolLookbackDays] = d.VolLookbackDays.ToString(CultureInfo.InvariantCulture),
                [KeyVolHistoryMaxReadings] = d.VolHistoryMaxReadings.ToString(CultureInfo.InvariantCulture),
                [KeyVolHistoryMinReadings] = d.VolHistoryMinReadings.ToString(CultureInfo.InvariantCulture),
                [KeyVolPercentileThreshold] = d.VolPercentileThreshold.ToString(CultureInfo.InvariantCulture),
                [KeyTierHighMultiplier] = ToStr(d.TierHighMultiplier),
                [KeyTierMidMultiplier] = ToStr(d.TierMidMultiplier),
                [KeyTierLowMultiplier] = ToStr(d.TierLowMultiplier),
                [KeyTierProfitHighThreshold] = ToStr(d.TierProfitHighThreshold),
                [KeyAvgDownTrigger] = ToStr(d.AvgDownTrigger),
                [KeyAvgDownFraction] = ToStr(d.AvgDownFraction),
                [KeyMondayAvgDownTrigger] = ToStr(d.MondayAvgDownTrigger),
                [KeyMondayAvgDownFraction] = ToStr(d.MondayAvgDownFraction),
                [KeyCloseStopPct] = ToStr(d.CloseStopPct),
                [KeyEntryDayCloseStopPct] = ToStr(d.EntryDayCloseStopPct),
                [KeyForceCloseWeekly] = d.ForceCloseWeekly.ToString(),
                [KeyForceCloseHourEt] = d.ForceCloseHourEt.ToString(CultureInfo.InvariantCulture)
            };

            // No-op after the first call for this instance (INSERT OR IGNORE) -- never
            // overwrites a value someone has since tuned directly in StrategyConfig.
            await _configRepository.SeedDefaultsAsync(strategyInstanceId, defaults, ct);
            var stored = await _configRepository.GetAllAsync(strategyInstanceId, ct);

            return new TqqqWeeklyRuntimeConfig
            {
                BaseSizeFraction = GetDecimal(stored, KeyBaseSizeFraction, d.BaseSizeFraction),
                VolBoostFraction = GetDecimal(stored, KeyVolBoostFraction, d.VolBoostFraction),
                VolLookbackDays = GetInt(stored, KeyVolLookbackDays, d.VolLookbackDays),
                VolHistoryMaxReadings = GetInt(stored, KeyVolHistoryMaxReadings, d.VolHistoryMaxReadings),
                VolHistoryMinReadings = GetInt(stored, KeyVolHistoryMinReadings, d.VolHistoryMinReadings),
                VolPercentileThreshold = GetDouble(stored, KeyVolPercentileThreshold, d.VolPercentileThreshold),
                TierHighMultiplier = GetDecimal(stored, KeyTierHighMultiplier, d.TierHighMultiplier),
                TierMidMultiplier = GetDecimal(stored, KeyTierMidMultiplier, d.TierMidMultiplier),
                TierLowMultiplier = GetDecimal(stored, KeyTierLowMultiplier, d.TierLowMultiplier),
                TierProfitHighThreshold = GetDecimal(stored, KeyTierProfitHighThreshold, d.TierProfitHighThreshold),
                AvgDownTrigger = GetDecimal(stored, KeyAvgDownTrigger, d.AvgDownTrigger),
                AvgDownFraction = GetDecimal(stored, KeyAvgDownFraction, d.AvgDownFraction),
                MondayAvgDownTrigger = GetDecimal(stored, KeyMondayAvgDownTrigger, d.MondayAvgDownTrigger),
                MondayAvgDownFraction = GetDecimal(stored, KeyMondayAvgDownFraction, d.MondayAvgDownFraction),
                CloseStopPct = GetDecimal(stored, KeyCloseStopPct, d.CloseStopPct),
                EntryDayCloseStopPct = GetDecimal(stored, KeyEntryDayCloseStopPct, d.EntryDayCloseStopPct),
                ForceCloseWeekly = GetBool(stored, KeyForceCloseWeekly, d.ForceCloseWeekly),
                ForceCloseHourEt = GetInt(stored, KeyForceCloseHourEt, d.ForceCloseHourEt)
            };
        }

        private static string ToStr(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        private static decimal GetDecimal(Dictionary<string, string> stored, string key, decimal fallback) =>
            stored.TryGetValue(key, out var v) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static double GetDouble(Dictionary<string, string> stored, string key, double fallback) =>
            stored.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static int GetInt(Dictionary<string, string> stored, string key, int fallback) =>
            stored.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static bool GetBool(Dictionary<string, string> stored, string key, bool fallback) =>
            stored.TryGetValue(key, out var v) && bool.TryParse(v, out var parsed) ? parsed : fallback;
    }
}
