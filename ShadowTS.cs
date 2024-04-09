using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace ShadowTS
{
    public class ShieldedPosition(Position position)
    {
        public string Id = position.Id;
        public Position Position = position;
        public int ElapsedBar { get; set; } = 0;
        public string StopOrderId { get; set; } = null;

        public override string ToString() => $"ElapsedBar = {this.ElapsedBar}. {this.Position}";

        public override int GetHashCode() => HashCode.Combine(this.Id);

        public override bool Equals(object obj) => obj is ShieldedPosition sp && this.Id == sp.Id;

        public static bool operator ==(ShieldedPosition left, ShieldedPosition right) => left.Equals(right);

        public static bool operator !=(ShieldedPosition left, ShieldedPosition right) => !(left == right);
    }

    public class ShadowTS : Strategy
    {
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
            if (this.Symbol == null)
            {
                this.LogError("Symbol is null");
                this.Stop();
                return;
            }

            this.ActivePositions.Clear();
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

        [Obsolete]
        protected override List<StrategyMetric> OnGetMetrics()
        {
            List<StrategyMetric> result = base.OnGetMetrics();
            result.Add(new StrategyMetric() { Name = "Positions Count", FormattedValue = this.ActivePositions.Count.ToString() });
            return result;
        }

        private void HistoricalData_NewHistoryItem(object sender, HistoryEventArgs e)
        {
            DateTime from = Core.TimeUtils.DateTimeUtcNow.Subtract(this.Period.Duration);
            var lastBar = this.Symbol.GetHistory(this.Period, from).Cast<HistoryItemBar>().FirstOrDefault(bar => bar.TimeRight < Core.TimeUtils.DateTimeUtcNow) ?? null;

            if (lastBar == null) {
                this.LogError("Failed to find completed bar");
                return;
            };

            this.Log($"New \"{this.Period}\" candle -- {lastBar}");

            foreach (var sp in this.ActivePositions)
            {
                ++sp.ElapsedBar;
                CancelStopOrder(sp);
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = sp.Position.Account,
                    Symbol = sp.Position.Symbol,
                    PositionId = sp.Id,
                    Side = sp.Position.Side == Side.Buy ? Side.Sell : Side.Buy,
                    TriggerPrice = sp.Position.Side == Side.Buy ? lastBar.Low : lastBar.High,
                    TimeInForce = TimeInForce.GTC,
                    Quantity = sp.Position.Quantity,
                    OrderTypeId = OrderType.Stop,
                });
                if (result.Status == TradingOperationResultStatus.Success)
                {
                    sp.StopOrderId = result.OrderId;
                }
                else
                {
                    sp.Position.Close();
                    this.LogError("Unable to set stop loss. Position closed");
                }
            }
        }

        private void Core_PositionAdded(Position position)
        {
            if (this.ActivePositions.Add(new ShieldedPosition(position)))
            {
                this.Log($"Added position -- ID: {position.Id}, Side: {position.Side}. ActivePos Count: {this.ActivePositions.Count}");
            }
        }

        private void Core_PositionRemoved(Position position)
        {
            this.Log($"Trying to remove {position.Id}");
            var sp = this.ActivePositions.FirstOrDefault(sp => sp.Id.Equals(position.Id)) ?? null;
            if (sp == null) return;
            CancelStopOrder(sp);
            if (this.ActivePositions.Remove(sp))
            {
                this.Log($"Removed position -- ID: {sp.Id}, Side: {sp.Position.Side}. ActivePos Count: {this.ActivePositions.Count}");
            }
        }

        private static void CancelStopOrder(ShieldedPosition sp)
        {
            if (sp.StopOrderId == null) return;
            try {
                Core.CancelOrder(Core.GetOrderById(sp.StopOrderId, sp.Position.ConnectionId));
            } catch (Exception) { }
            sp.StopOrderId = null;
        }
    }
}