namespace LeverageTradingStrategies.Infrastructure.Helpers
{
    /// <summary>
    /// Single, shared source of truth for equity market status. Both IBroker and
    /// IStockDataProvider implementations delegate here rather than maintaining separate
    /// logic — a prior duplication had SchwabBroker comparing DateTime.Now (raw server-local,
    /// no Eastern conversion) against session times, while StockDataProvider correctly used
    /// an explicit Eastern conversion. Only one shared, timezone-safe implementation now.
    /// </summary>
    public static class MarketHoursHelper
    {
        private static readonly TimeZoneInfo EasternTimeZone = GetEasternTimeZone();

        public static DateTime GetEasternNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTimeZone);
        }

        public static DateTime NormalizeSchwabDateTimeToEastern(DateTime value)
        {
            if (value == default)
                return default;

            if (value.Kind == DateTimeKind.Utc)
            {
                return DateTime.SpecifyKind(
                    TimeZoneInfo.ConvertTimeFromUtc(value, EasternTimeZone),
                    DateTimeKind.Unspecified);
            }

            if (value.Kind == DateTimeKind.Local)
            {
                DateTime utc = value.ToUniversalTime();
                return DateTime.SpecifyKind(
                    TimeZoneInfo.ConvertTimeFromUtc(utc, EasternTimeZone),
                    DateTimeKind.Unspecified);
            }

            // Kind.Unspecified from the Schwab SDK: treated as already-Eastern wall-clock
            // time, per documented, empirically-confirmed API behavior.
            return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }

        /// <summary>
        /// Explicitly converts an Eastern wall-clock DateTime to a real UTC instant.
        /// Only call this on values that are genuinely Eastern wall-clock time (i.e.
        /// already passed through NormalizeSchwabDateTimeToEastern or an equivalent
        /// Eastern-tagging step) — calling it on a raw, still-UTC value will double-convert.
        /// </summary>
        public static DateTime EasternToUtc(DateTime easternWallClockTime)
        {
            var unspecified = DateTime.SpecifyKind(easternWallClockTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, EasternTimeZone);
        }

        /// <summary>
        /// Determines equity market status (OPEN/AM/PM/CLOSED) from a Schwab-style market
        /// hours response. Generic over the session-window shape so both IBroker and
        /// IStockDataProvider's differently-typed marketHours objects can share this logic
        /// without a hard dependency between the two projects — callers pass in the
        /// relevant start/end times already extracted from their own response type.
        /// </summary>
        public static string DetermineMarketStatus(
            bool isEquityOpen,
            (DateTime Start, DateTime End)? regularMarket,
            (DateTime Start, DateTime End)? preMarket,
            (DateTime Start, DateTime End)? postMarket)
        {
            if (!isEquityOpen)
                return "CLOSED";

            DateTime nowEastern = GetEasternNow();

            if (regularMarket.HasValue)
            {
                var start = NormalizeSchwabDateTimeToEastern(regularMarket.Value.Start);
                var end = NormalizeSchwabDateTimeToEastern(regularMarket.Value.End);
                if (nowEastern >= start && nowEastern < end)
                    return "OPEN";
            }

            if (preMarket.HasValue)
            {
                var start = NormalizeSchwabDateTimeToEastern(preMarket.Value.Start);
                var end = NormalizeSchwabDateTimeToEastern(preMarket.Value.End);
                if (nowEastern >= start && nowEastern < end)
                    return "AM";
            }

            if (postMarket.HasValue)
            {
                var start = NormalizeSchwabDateTimeToEastern(postMarket.Value.Start);
                var end = NormalizeSchwabDateTimeToEastern(postMarket.Value.End);
                if (nowEastern >= start && nowEastern < end)
                    return "PM";
            }

            return "CLOSED";
        }

        private static TimeZoneInfo GetEasternTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); // Windows
            }
            catch
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); // Linux/Docker/Azure
            }
        }
    }
}