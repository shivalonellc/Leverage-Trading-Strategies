using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Computes every field in TqqqAgentMarketSnapshot (spec §5) from Tradier quotes +
    /// today's 1-min bars for TQQQ and QQQ. All indicator math here is intentionally simplified
    /// (EMA seeded from the first value rather than a strict SMA seed, ATR as a plain average of
    /// true ranges rather than Wilder-smoothed) -- these are context signals for Claude's
    /// discretionary reasoning, not inputs to a hardcoded rule, so exact parity with a charting
    /// platform isn't required. RSI14 does use proper Wilder smoothing since it's the one
    /// indicator where the simplified vs. Wilder difference is large enough to matter.</summary>
    public class TqqqAgentMarketDataService : ITqqqAgentMarketDataService
    {
        private const string TradedSymbol = "TQQQ";
        private const string RegimeSymbol = "QQQ";
        private const int TradingMinutesPerDay = 390; // 9:30-16:00 ET

        private readonly ITradierMarketDataRestClient _client;
        private readonly ILogger<TqqqAgentMarketDataService> _logger;

        public TqqqAgentMarketDataService(ITradierMarketDataRestClient client, ILogger<TqqqAgentMarketDataService> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task<TqqqAgentMarketSnapshot> GetSnapshotAsync(DateTime nowEastern, TimeSpan marketOpenEt, TimeSpan forceFlattenEt, CancellationToken ct = default)
        {
            var sessionStart = nowEastern.Date + marketOpenEt;

            var tqqqQuoteTask = _client.GetQuoteAsync(TradedSymbol, ct);
            var qqqQuoteTask = _client.GetQuoteAsync(RegimeSymbol, ct);
            var tqqqBarsTask = _client.GetTimeSalesAsync(TradedSymbol, "1min", sessionStart, nowEastern, ct);
            var qqqBarsTask = _client.GetTimeSalesAsync(RegimeSymbol, "1min", sessionStart, nowEastern, ct);
            await Task.WhenAll(tqqqQuoteTask, qqqQuoteTask, tqqqBarsTask, qqqBarsTask);

            var tqqqQuote = tqqqQuoteTask.Result;
            var qqqQuote = qqqQuoteTask.Result;
            var tqqqBars = tqqqBarsTask.Result;
            var qqqBars = qqqBarsTask.Result;

            if (tqqqQuote == null)
                _logger.LogWarning("TqqqAgentMarketDataService: no TQQQ quote returned this cycle -- snapshot will use bar-derived fallbacks only.");
            if (qqqQuote == null)
                _logger.LogWarning("TqqqAgentMarketDataService: no QQQ quote returned this cycle -- regime fields will use bar-derived fallbacks only.");

            var tqqqCloses = tqqqBars.Select(b => b.Close).ToList();

            var lastPrice = tqqqQuote?.Last ?? (tqqqBars.Count > 0 ? tqqqBars[^1].Close : 0m);
            var openPrice = tqqqQuote?.Open ?? (tqqqBars.Count > 0 ? tqqqBars[0].Open : lastPrice);
            var dayHigh = tqqqBars.Count > 0 ? tqqqBars.Max(b => b.High) : (tqqqQuote?.High ?? lastPrice);
            var dayLow = tqqqBars.Count > 0 ? tqqqBars.Min(b => b.Low) : (tqqqQuote?.Low ?? lastPrice);
            var priorClose = tqqqQuote?.PrevClose ?? lastPrice;

            var (macd, macdSignal, macdHist) = ComputeMacd(tqqqCloses);

            var qqqLast = qqqQuote?.Last ?? (qqqBars.Count > 0 ? qqqBars[^1].Close : 0m);
            var qqqVwap = ComputeCumulativeVwap(qqqBars, qqqLast);
            var qqqEma20 = qqqBars.Count > 0 ? ComputeEma(qqqBars.Select(b => b.Close).ToList(), 20) : qqqLast;

            var minutesSinceOpen = Math.Max(0, (int)(nowEastern - sessionStart).TotalMinutes);
            var flattenAt = nowEastern.Date + forceFlattenEt;
            var minutesUntilFlatten = Math.Max(0, (int)(flattenAt - nowEastern).TotalMinutes);

            return new TqqqAgentMarketSnapshot
            {
                LastPrice = Round3(lastPrice),
                Vwap = Round3(ComputeCumulativeVwap(tqqqBars, lastPrice)),
                Ema9 = Round3(tqqqBars.Count > 0 ? ComputeEma(tqqqCloses, 9) : lastPrice),
                Ema20 = Round3(tqqqBars.Count > 0 ? ComputeEma(tqqqCloses, 20) : lastPrice),
                Rsi14 = Round3(ComputeRsi(tqqqCloses, 14)),
                Macd = Round3(macd),
                MacdSignal = Round3(macdSignal),
                MacdHistogram = Round3(macdHist),
                Atr14 = Round3(ComputeAtr(tqqqBars, 14)),
                DayHigh = Round3(dayHigh),
                DayLow = Round3(dayLow),
                OpenPrice = Round3(openPrice),
                PriorClose = Round3(priorClose),
                GapFromPriorClosePct = Round3(priorClose > 0 ? (openPrice - priorClose) / priorClose * 100m : 0m),
                DistanceFromOpenPct = Round3(openPrice > 0 ? (lastPrice - openPrice) / openPrice * 100m : 0m),
                RelativeVolume = Round3(ComputeRelativeVolume(tqqqQuote, minutesSinceOpen)),

                QqqLastPrice = Round3(qqqLast),
                QqqVwap = Round3(qqqVwap),
                QqqEma20 = Round3(qqqEma20),
                QqqAboveVwap = qqqLast >= qqqVwap,
                QqqAboveEma20 = qqqLast >= qqqEma20,

                MinutesSinceOpen = minutesSinceOpen,
                MinutesUntilForceFlatten = minutesUntilFlatten
            };
        }

        // Rounded once here at the boundary where indicators leave the calculation pipeline
        // (not inside ComputeEma/ComputeEmaSeries etc.) so chained math like MACD's fast/slow
        // EMA subtraction still runs on full decimal precision internally -- only the final
        // values handed to Claude/persisted to TqqqAgentDecisions get truncated for readability.
        private static decimal Round3(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
        private static decimal? Round3(decimal? v) => v.HasValue ? Math.Round(v.Value, 3, MidpointRounding.AwayFromZero) : null;

        private static decimal? ComputeRelativeVolume(TradierQuoteDto? quote, int minutesSinceOpen)
        {
            if (quote?.Volume is not long volume || quote.AverageVolume is not long avgVolume || avgVolume <= 0)
                return null;

            // Fraction of a typical trading day elapsed -- floored so the very first minute of
            // the session doesn't produce a wildly inflated ratio from dividing by ~0.
            var fractionOfDay = Math.Max(minutesSinceOpen / (decimal)TradingMinutesPerDay, 0.02m);
            var expectedVolumeSoFar = avgVolume * fractionOfDay;
            return expectedVolumeSoFar > 0 ? volume / expectedVolumeSoFar : null;
        }

        private static decimal ComputeCumulativeVwap(IReadOnlyList<TradierTimeSalesBarDto> bars, decimal fallbackLast)
        {
            if (bars.Count == 0)
                return fallbackLast;

            decimal sumPriceVolume = 0m, sumVolume = 0m;
            foreach (var bar in bars)
            {
                var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
                sumPriceVolume += typicalPrice * bar.Volume;
                sumVolume += bar.Volume;
            }

            return sumVolume > 0 ? sumPriceVolume / sumVolume : fallbackLast;
        }

        /// <summary>Simplified EMA: seeded from the first value in the series rather than an
        /// SMA of the first `period` values, then recursed across the rest. Returns the final
        /// (most recent) value only.</summary>
        private static decimal ComputeEma(IReadOnlyList<decimal> values, int period)
        {
            if (values.Count == 0)
                return 0m;

            var series = ComputeEmaSeries(values, period);
            return series[^1];
        }

        private static List<decimal> ComputeEmaSeries(IReadOnlyList<decimal> values, int period)
        {
            var series = new List<decimal>(values.Count);
            if (values.Count == 0)
                return series;

            var k = 2m / (period + 1);
            var ema = values[0];
            series.Add(ema);
            for (var i = 1; i < values.Count; i++)
            {
                ema = values[i] * k + ema * (1 - k);
                series.Add(ema);
            }
            return series;
        }

        /// <summary>Wilder's RSI. Null (not a default like 50) if there isn't yet a full
        /// `period`+1 closes of history this session -- callers/prompts should treat null as
        /// "not enough data yet today" rather than a neutral reading.</summary>
        private static decimal? ComputeRsi(IReadOnlyList<decimal> closes, int period)
        {
            if (closes.Count < period + 1)
                return null;

            decimal avgGain = 0m, avgLoss = 0m;
            for (var i = 1; i <= period; i++)
            {
                var change = closes[i] - closes[i - 1];
                if (change > 0) avgGain += change; else avgLoss += -change;
            }
            avgGain /= period;
            avgLoss /= period;

            for (var i = period + 1; i < closes.Count; i++)
            {
                var change = closes[i] - closes[i - 1];
                var gain = change > 0 ? change : 0m;
                var loss = change < 0 ? -change : 0m;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            if (avgLoss == 0m)
                return 100m;

            var rs = avgGain / avgLoss;
            return 100m - 100m / (1 + rs);
        }

        private static decimal? ComputeAtr(IReadOnlyList<TradierTimeSalesBarDto> bars, int period)
        {
            if (bars.Count < period + 1)
                return null;

            var trueRanges = new List<decimal>(bars.Count - 1);
            for (var i = 1; i < bars.Count; i++)
            {
                var high = bars[i].High;
                var low = bars[i].Low;
                var prevClose = bars[i - 1].Close;
                var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                trueRanges.Add(tr);
            }

            var lastN = trueRanges.Skip(Math.Max(0, trueRanges.Count - period)).ToList();
            return lastN.Count > 0 ? lastN.Average() : null;
        }

        /// <summary>Standard MACD(12,26,9). Null (all three) until there's enough same-session
        /// history (slow period + signal period closes) -- a fresh 9:35 entry-window open won't
        /// have this yet, which is fine, Claude just sees nulls for those fields that cycle.</summary>
        private static (decimal? Macd, decimal? Signal, decimal? Histogram) ComputeMacd(IReadOnlyList<decimal> closes)
        {
            const int fastPeriod = 12, slowPeriod = 26, signalPeriod = 9;
            if (closes.Count < slowPeriod + signalPeriod)
                return (null, null, null);

            var emaFastSeries = ComputeEmaSeries(closes, fastPeriod);
            var emaSlowSeries = ComputeEmaSeries(closes, slowPeriod);
            var macdSeries = new List<decimal>(closes.Count);
            for (var i = 0; i < closes.Count; i++)
                macdSeries.Add(emaFastSeries[i] - emaSlowSeries[i]);

            var signalSeries = ComputeEmaSeries(macdSeries, signalPeriod);
            var macd = macdSeries[^1];
            var signal = signalSeries[^1];
            return (macd, signal, macd - signal);
        }
    }
}
