using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace ShadowTS
{
    public class WatchedPosition(Position position)
    {
        public string Id = position.Id;
        public Position Data = position;
        public string StopOrderId = null;
        public Queue<double> NextStops = new();
        public bool IsLong = position.Side == Side.Buy;
        public bool IsShort = position.Side == Side.Sell;
        public Side StopSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;

        public override string ToString() => $"Id = {this.Id}, StopId = {this.StopOrderId}, NextStops = {this.NextStops}, Data = {this.Data}";

        public override int GetHashCode() => HashCode.Combine(this.Id);

        public override bool Equals(object obj) => obj is WatchedPosition sp && this.Id == sp.Id;

        public static bool operator ==(WatchedPosition left, WatchedPosition right) => left.Equals(right);

        public static bool operator !=(WatchedPosition left, WatchedPosition right) => !(left == right);
    }

    public class ShadowTS : Strategy
    {
        [InputParameter("Symbol")]
        public Symbol Symbol;

        [InputParameter("Trailing Stop Period")]
        public Period Period = Period.MIN1;

        [InputParameter("Trailing Stop Bar Lag")]
        public int BarLag = 2;

        private HistoricalData HistoricalData;
        private WatchedPosition Position = null;

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

            this.Position = null;
            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            this.HistoricalData = this.Symbol.GetHistory(this.Period, Core.TimeUtils.DateTimeUtcNow);
            this.HistoricalData.NewHistoryItem += this.HistoricalData_NewHistoryItem;
        }

        protected override void OnStop()
        {
            Core.PositionAdded -= this.Core_PositionAdded;
            Core.PositionRemoved -= this.Core_PositionRemoved;
            if (this.HistoricalData != null)
            {
                this.HistoricalData.NewHistoryItem -= this.HistoricalData_NewHistoryItem;
                this.HistoricalData.Dispose();
            }
        }

        [Obsolete]
        protected override List<StrategyMetric> OnGetMetrics()
        {
            List<StrategyMetric> result = base.OnGetMetrics();
            result.Add(new StrategyMetric() { Name = "Symbol", FormattedValue = this.Symbol.ToString() });
            result.Add(new StrategyMetric() { Name = "Period", FormattedValue = this.Period.ToString() });
            result.Add(new StrategyMetric() { Name = "Bar Lag", FormattedValue = this.BarLag.ToString() });
            return result;
        }

        private void HistoricalData_NewHistoryItem(object sender, HistoryEventArgs e)
        {
            var now = Core.TimeUtils.DateTimeUtcNow;
            var from = now.Subtract(this.Period.Duration);
            var bars = this.Symbol.GetHistory(this.Period, from).Cast<HistoryItemBar>();
            var lastBar = bars.LastOrDefault(bar => bar.TimeLeft >= from.Subtract(TimeSpan.FromSeconds(5)) && bar.TimeRight <= now.AddSeconds(5)) ?? null;

            if (lastBar == null) {
                this.LogError("Failed to find completed bar");
                return;
            };
            this.Log($"New \"{this.Period}\" candle -- {lastBar}");

            if (this.Position == null) return;
            double nextStop;
            Order existingStopOrder = null;

            if (this.Position.StopOrderId == null)
            {
                existingStopOrder = this.GetExistingStopOrder();
                existingStopOrder?.Cancel();
            }

            while (this.Position.NextStops.Count < this.BarLag)
            {
                nextStop = this.Position.IsLong ? lastBar.Low : lastBar.High;
                if (this.Position.NextStops.Count > 0)
                {
                    double altPrice = existingStopOrder != null ? existingStopOrder.TriggerPrice : this.Position.NextStops.Last();
                    nextStop = this.Position.IsLong ? Math.Max(lastBar.Low, altPrice) : Math.Min(lastBar.High, altPrice);
                }
                this.Position.NextStops.Enqueue(nextStop);
            }

            CancelStopOrder(this.Position);

            nextStop = this.Position.NextStops.Dequeue();

            if ((this.Position.IsLong && nextStop < lastBar.Close) || (this.Position.IsShort && nextStop > lastBar.Close))
            {
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = this.Position.Data.Account,
                    Symbol = this.Position.Data.Symbol,
                    PositionId = this.Position.Id,
                    Side = this.Position.StopSide,
                    TriggerPrice = nextStop,
                    TimeInForce = TimeInForce.GTC,
                    Quantity = this.Position.Data.Quantity,
                    OrderTypeId = OrderType.Stop,
                });
                if (result.Status == TradingOperationResultStatus.Success)
                {
                    this.Position.StopOrderId = result.OrderId;
                    return;
                }
            } else
            {
                this.Position.Data.Close();
                this.Log($"Price has already retraced. Position closed.");
                return;
            }

            this.LogError("Unable to set stop loss.");
        }

        private void Core_PositionAdded(Position position)
        {
            this.Position = new WatchedPosition(position);
            this.Log($"Added position -- ID: {this.Position.Id}, Side: {this.Position.Data.Side}");
        }

        private void Core_PositionRemoved(Position position)
        {
            this.Log($"Trying to remove {position.Id}");
            if (this.Position == null || !this.Position.Id.Equals(position.Id)) return;
            CancelStopOrder(this.Position);
            this.Position = null;
            this.Log($"Removed position -- ID: {this.Position.Id}, Side: {this.Position.Data.Side}");
        }

        private Order GetExistingStopOrder() => Core.Orders
                    .Where(order => this.Position != null && order.Symbol.Equals(this.Symbol) && order.OrderTypeId == OrderType.Stop && order.Side == this.Position.StopSide)
                    .FirstOrDefault() ?? null;

        private static void CancelStopOrder(WatchedPosition position)
        {
            if (position.StopOrderId == null) return;
            try {
                Core.CancelOrder(Core.GetOrderById(position.StopOrderId, position.Data.ConnectionId));
            } catch (Exception) { }
            position.StopOrderId = null;
        }
    }
}