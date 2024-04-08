using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace ShadowTS
{
    public struct ShieldedPosition(Position position)
    {
        public string Id = position.Id;
        public Position Position = position;
        public int ElapsedBar { get; private set; } = 0;
        public Order StopOrder { get; private set; }

        public void IncreaseElapsedBar() => ++this.ElapsedBar;
        public void UpdateStop(Order stopOrder)
        {
            this.Position.StopLoss?.Cancel();
            this.StopOrder?.Cancel();
            this.StopOrder = stopOrder;
        }

        public override readonly string ToString() => $"ElapsedBar = {this.ElapsedBar}. {this.Position}";

        public override readonly int GetHashCode() => HashCode.Combine(this.Id);

        public override readonly bool Equals(object obj) => obj is ShieldedPosition sp && this.Id == sp.Id;

        public static bool operator ==(ShieldedPosition left, ShieldedPosition right) => left.Equals(right);

        public static bool operator !=(ShieldedPosition left, ShieldedPosition right) => !(left == right);
    }

    public class ShadowTS : Strategy
    {
        [InputParameter("Account")]
        public Account Account;

        [InputParameter("Symbol")]
        public Symbol Symbol;

        [InputParameter("Trailing Stop Period")]
        public Period Period = Period.HOUR4;

        [InputParameter("Trailing Stop Bar Lag")]
        public int barLag = 2;

        private HistoricalData HistoricalData;
        private readonly HashSet<ShieldedPosition> ActivePositions = [];

        public ShadowTS() : base()
        {
            this.Name = "ShadowTS";
            this.Description = "A Trailing Stop strategy automatically adjusting the Stop Loss level on previous wicks extremities.";
        }

        protected override void OnRun()
        {
            if (this.Account == null)
            {
                this.LogError("Account is null");
                this.Stop();
                return;
            }

            if (this.Symbol == null)
            {
                this.LogError("Symbol is null");
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

            this.Log($"New \"{this.Period}\" candle -- {lastBar}");

            foreach (var sp in this.ActivePositions)
            {
                this.ActivePositions.Remove(sp);

                sp.IncreaseElapsedBar();
                // TODO: Send the order to the platform *after* having cancelled any active stop order for that position. Make ShieldedPosition.UpdateStop place the order.
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = this.Account,
                    Symbol = this.Symbol,
                    PositionId = sp.Id,
                    Side = sp.Position.Side == Side.Buy ? Side.Sell : Side.Buy,
                    Price = sp.Position.Side == Side.Buy ? lastBar.Low : lastBar.High,
                    TimeInForce = TimeInForce.GTC,
                    Quantity = sp.Position.Quantity,
                    OrderTypeId = OrderType.Stop,
                });
                if (result.Status == TradingOperationResultStatus.Success)
                {
                    sp.UpdateStop(Core.Instance.GetOrderById(result.OrderId));
                    this.ActivePositions.Add(sp);
                    this.Log($"{sp}");
                } else
                {
                    sp.Position.Close();
                    this.LogError("Unable to set stop loss. Position closed");
                }
            }
        }

        private void Core_PositionAdded(Position position)
        {
            if (!this.ActivePositions.Where(sp => sp.Id == position.Id).Any())
            {
                this.Log($"Adding position -- ID: {position.Id}, Side: {position.Side}");
            }

            this.ActivePositions.Add(new ShieldedPosition(position));
        }

        private void Core_PositionRemoved(Position position)
        {
            if (this.ActivePositions.Where(sp => sp.Id == position.Id).Any())
            {
                this.Log($"Removing position -- ID: {position.Id}, Side: {position.Side}");
            }

            this.ActivePositions.RemoveWhere(sp => sp.Id == position.Id);
        }
    }
}