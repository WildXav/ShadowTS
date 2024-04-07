using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace ShadowTS
{
    public class ShadowTS : Strategy
    {
        [InputParameter("Symbol")]
        public Symbol Symbol;

        [InputParameter("Trailing Stop Period")]
        public Period Period = Period.HOUR4;

        private HistoricalData HistoricalData;

        public ShadowTS() : base()
        {
            this.Name = "ShadowTS";
            this.Description = "A Trailing Stop strategy automatically adjusting the Stop Loss level on previous wicks extremities.";
        }

        protected override void OnRun()
        {
            if (this.Symbol == null)
            {
                this.Log("Symbol is null", StrategyLoggingLevel.Error);
                this.Stop();
                return;
            }

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            this.HistoricalData = this.Symbol.GetHistory(this.Period, Core.TimeUtils.DateTimeUtcNow);
            this.HistoricalData.NewHistoryItem += this.HistoricalData_NewHistoryItem;
        }

        protected override void OnStop()
        {
            if (this.HistoricalData != null)
            {
                Core.PositionAdded -= this.Core_PositionAdded;
                Core.PositionRemoved -= this.Core_PositionRemoved;

                this.HistoricalData.NewHistoryItem -= this.HistoricalData_NewHistoryItem;
                this.HistoricalData.Dispose();
            }
        }

        private void HistoricalData_NewHistoryItem(object sender, HistoryEventArgs e)
        {
            var lastBar = (HistoryItemBar)this.Symbol.GetHistory(this.Period, Core.TimeUtils.DateTimeUtcNow.Subtract(this.Period.Duration))[1];
            var openTime = lastBar.TimeRight;
            var openPrice = lastBar.Open;
            var highPrice = lastBar.High;
            var lowPrice = lastBar.Low;
            var closePrice = lastBar.Close;

            this.Log($"New {this.Period} candle -- T: {openTime}, O: {openPrice}, H: {highPrice}, L: {lowPrice}, C: {closePrice}");
        }

        private void Core_PositionAdded(Position position)
        {
        }

        private void Core_PositionRemoved(Position position)
        {

        }
    }
}