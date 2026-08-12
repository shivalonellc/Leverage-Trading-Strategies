namespace LeverageTradingStrategies.Infrastructure.Models;

public sealed class SymbolPositionInfo
{
    public MyAccountInfo AccountInfo { get; set; } = new MyAccountInfo();


    public string Symbol { get; set; } = string.Empty;


    public decimal AveragePrice { get; set; }

    public decimal MarketValue { get; set; }

    public decimal CostBasis { get; set; }

    public decimal UnrealizedProfitLoss { get; set; }

    public decimal UnrealizedProfitLossPercent { get; set; }
    public PositionSide Side { get; set; }
    public DateTime? AsOfUtc { get; set; }
    public decimal LongQuantity { get; internal set; }
    public decimal ShortQuantity { get; internal set; }


}

public sealed class MyAccountInfo
{
    public string AccountNumber { get; set; } = string.Empty;
    public decimal? Totalcash { get; internal set; }
    public decimal MarginableBuyingPower { get; internal set; }
    public decimal MaintenanceRequirement { get; internal set; }
    public decimal DayTradingBuyingPower { get; internal set; }
    public decimal BuyingPower { get; internal set; }
    public decimal NonMaginableBuyingPower { get; internal set; }
    public decimal CashBalance { get; internal set; }
}

public enum PositionSide
{
    Flat = 0,
    Long = 1,
    Short = 2
}