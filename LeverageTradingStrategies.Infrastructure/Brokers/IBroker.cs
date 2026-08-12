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

        // --- VERTICAL CREDIT SPREAD (2-leg combo order) ---
        // Sends ONE order with complexOrderStrategyType=VERTICAL and both legs in the same leg
        // collection, instead of two independently-placed single-leg option orders. Schwab
        // routes a combo order like this to the options exchange as a single spread order --
        // the exchange either executes both legs together or neither fills at all, which is
        // what actually rules out a short leg filling without its protective long leg (two
        // independent orders can never fully guarantee that, however carefully sequenced).
        // Works for BOTH a bull put credit spread (both legs puts) and a bear call credit
        // spread (both legs calls) -- the method only cares about the two OCC symbols, not
        // which right they are.

        /// <summary>Opens a vertical credit spread: BUY_TO_OPEN the long (protective) leg,
        /// SELL_TO_OPEN the short (premium-collecting) leg, as one NET_CREDIT combo order.</summary>
        Task<string> PlaceVerticalCreditSpreadOpenOrderAsync(string accountNumber, string longOptionSymbol, string shortOptionSymbol, int contracts, decimal netCredit, CancellationToken ct = default);

        /// <summary>Closes a vertical credit spread opened via the method above: SELL_TO_CLOSE
        /// the long leg, BUY_TO_CLOSE the short leg, as one NET_DEBIT combo order.</summary>
        Task<string> PlaceVerticalCreditSpreadCloseOrderAsync(string accountNumber, string longOptionSymbol, string shortOptionSymbol, int contracts, decimal netDebit, CancellationToken ct = default);
    }
}