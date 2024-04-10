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
        public int ElapsedBar = 0;
        public string StopOrderId = null;
        public Queue<double> NextStops = new();

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
        public int BarLag = 2;

        private HistoricalData HistoricalData;
        private readonly HashSet<ShieldedPosition> WatchedPositions = [];

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

            this.WatchedPositions.Clear();
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
            result.Add(new StrategyMetric() { Name = "Symbol", FormattedValue = this.Symbol.ToString() });
            result.Add(new StrategyMetric() { Name = "Period", FormattedValue = this.Period.ToString() });
            result.Add(new StrategyMetric() { Name = "Bar Lag", FormattedValue = this.BarLag.ToString() });
            result.Add(new StrategyMetric() { Name = "Watched Positions", FormattedValue = this.WatchedPositions.Count.ToString() });
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

            foreach (var sp in this.WatchedPositions)
            {
                while (sp.NextStops.Count < this.BarLag)
                {
                    var nextStop = sp.Position.Side == Side.Buy ? lastBar.Low : lastBar.High;
                    if (sp.NextStops.Count > 0)
                    {
                        nextStop = sp.Position.Side == Side.Buy ? Math.Max(lastBar.Low, sp.NextStops.Last()) : Math.Min(lastBar.High, sp.NextStops.Last());
                    }
                    sp.NextStops.Enqueue(nextStop);
                }

                ++sp.ElapsedBar;
                CancelStopOrder(sp);
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = sp.Position.Account,
                    Symbol = sp.Position.Symbol,
                    PositionId = sp.Id,
                    Side = sp.Position.Side == Side.Buy ? Side.Sell : Side.Buy,
                    TriggerPrice = sp.NextStops.Dequeue(),
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
            var sp = new ShieldedPosition(position);
            var desiredStopSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;
            sp.StopOrderId = Core.Orders
                    .Where(order => order.Symbol.Equals(position.Symbol) && order.OrderTypeId == OrderType.Stop && order.Side == desiredStopSide)
                    .Select(order => order.Id)
                    .FirstOrDefault() ?? null;

            if (this.WatchedPositions.Add(sp))
            {
                this.Log($"Added position -- ID: {sp.Id}, Side: {sp.Position.Side}, Stop: {sp.StopOrderId ?? "None"}");
            }
        }

        private void Core_PositionRemoved(Position position)
        {
            this.Log($"Trying to remove {position.Id}");
            var sp = this.WatchedPositions.FirstOrDefault(sp => sp.Id.Equals(position.Id)) ?? null;
            if (sp == null) return;
            CancelStopOrder(sp);
            if (this.WatchedPositions.Remove(sp))
            {
                this.Log($"Removed position -- ID: {sp.Id}, Side: {sp.Position.Side}");
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