namespace LeverageTradingStrategies.Infrastructure.Options
{
    /// <summary>Option chain/greeks data, sourced from Tradier (not Schwab) -- Tradier's
    /// market-data API returns live per-contract greeks (delta/gamma/theta/vega/IV) directly
    /// on the chain response, which is what the vertical-spread builder and marking job need
    /// for strike selection and live mark-to-market. Underlying spot price still comes from
    /// the existing Schwab-backed IQuoteProvider -- this interface is options-chain only.
    /// Real order EXECUTION still goes through Schwab (IBroker) regardless of where the chain
    /// data came from.</summary>
    public interface ITradierOptionsProvider
    {
        Task<IReadOnlyList<OptionExpirationDto>> GetExpirationsAsync(string underlyingSymbol, CancellationToken ct = default);

        Task<OptionChainDto> GetOptionChainAsync(string underlyingSymbol, DateTime expirationDate, CancellationToken ct = default);
    }
}
