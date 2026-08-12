using LeverageTradingStrategies.Infrastructure.Models;



namespace LeverageTradingStrategies.Infrastructure.Brokers
{
    public interface IBroker
    {
       
        // --- PORTFOLIO & POSITION RISK MANAGEMENT ---
        Task<SymbolPositionInfo?> GetSymbolPositionAsync(string accountNumber, string symbol);
        Task<string> GetEquityMarketStatus();

        /// <summary>Total account equity (Schwab's currentBalances.liquidationValue) — used
        /// by the weekly strategy's sizing math (entry qty, avg-down qty are both fractions
        /// of this value). Not just cash: includes the mark-to-market value of any open
        /// position, matching how the backtest's own portfolio_value() helper works.</summary>
        Task<decimal> GetPortfolioValueAsync(string accountNumber, CancellationToken ct = default);
        // --- ORDER EXECUTION SUITE ---
        Task<string> PlaceBuyMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default);
        Task<string> PlaceBuyLimitOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default);
        Task<string> PlaceSellMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default);
        Task<string> PlaceSellLimitOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default);
        Task<string> PlaceStopLossOrderAsync(string accountNumber, string symbol, int quantity, decimal stopPrice, CancellationToken ct = default);

        /// <summary>Submits a limit SELL order at an explicit target price to close an
        /// existing long position if/when it fills — the take-profit counterpart to
        /// PlaceStopLossOrderAsync's explicit stopPrice. NOT pegged to the current mark price
        /// (unlike PlaceSellLimitOrderAsync above, which always uses the live quote's mark).</summary>
        Task<string> PlaceTakeProfitOrderAsync(string accountNumber, string symbol, int quantity, decimal limitPrice, CancellationToken ct = default);

        /// <summary>Opens a short position (TO_OPEN with a negative quantity under the hood —
        /// see SchwabBroker remarks). Needed for symbols traded directly both directions
        /// (e.g. SPY mapped to itself for both Up and Down) rather than via a leveraged
        /// bull/bear instrument pair like QQQ/TQQQ/SQQQ, which never needs a real short.</summary>
        Task<string> PlaceShortSellMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default);

        /// <summary>Closes/covers a short position opened via PlaceShortSellMarketOrderAsync
        /// (TO_CLOSE with a positive quantity — produces a BUY_TO_COVER instruction, not a
        /// plain BUY).</summary>
        Task<string> PlaceBuyToCoverMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default);

        Task<decimal?> GetOrderFillPriceAsync(string accountNumber, string orderId, CancellationToken ct = default);


    }
}