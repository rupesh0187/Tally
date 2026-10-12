namespace TallyNext.Domain.Inventory;

/// <param name="ValueMinor">Inward value at cost (purchase taxable value), or null for movements valued at the item's
/// current cost: sales returns, and every outward movement.</param>
public sealed record StockMovement(DateOnly Date, StockDirection Direction, long QuantityMilli, long? ValueMinor);

public readonly record struct StockPosition(long QuantityMilli, long ValueMinor);

public sealed record StockPeriodResult(
    StockPosition Opening,
    long InwardQuantityMilli,
    long InwardValueMinor,
    long OutwardQuantityMilli,
    long OutwardCostMinor,
    StockPosition Closing,
    bool WentNegative);

/// <summary>
/// Inventory valuation (periodic reporting over perpetual cost flows).
/// <list type="bullet">
/// <item>Weighted average: perpetual moving average; each outward is costed at the average at that moment.</item>
/// <item>FIFO: outward consumes the oldest remaining lots first.</item>
/// <item>Inward value is the purchase taxable value (GST excluded; input tax is recoverable). Sales returns come
/// back at the current cost; purchase returns go out at cost like any outward.</item>
/// <item>Negative stock is allowed (offline devices may each sell the last unit): the shortfall is valued at the last
/// known unit cost and is filled first by the next inward.</item>
/// </list>
/// Values are carried as decimals internally and rounded to the paisa when reported.
/// </summary>
public static class StockValuation
{
    public static StockPeriodResult Run(
        ValuationMethod method,
        StockPosition opening,
        IEnumerable<StockMovement> orderedMovements,
        DateOnly from,
        DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(orderedMovements);
        ICostFlow flow = method == ValuationMethod.Fifo ? new FifoFlow(opening) : new AverageFlow(opening);

        StockPosition? atFrom = null;
        long inQty = 0, outQty = 0;
        decimal inValue = 0, outCost = 0;
        var wentNegative = false;
        foreach (var movement in orderedMovements)
        {
            if (movement.Date > to)
            {
                break;
            }

            if (atFrom is null && movement.Date >= from)
            {
                atFrom = flow.Position;
            }

            if (movement.QuantityMilli <= 0)
            {
                continue;
            }

            if (movement.Direction == StockDirection.In)
            {
                var value = flow.In(movement.QuantityMilli, movement.ValueMinor);
                if (movement.Date >= from)
                {
                    inQty += movement.QuantityMilli;
                    inValue += value;
                }
            }
            else if (movement.Direction == StockDirection.Out)
            {
                var cost = flow.Out(movement.QuantityMilli);
                if (movement.Date >= from)
                {
                    outQty += movement.QuantityMilli;
                    outCost += cost;
                }
            }

            wentNegative |= flow.QuantityMilli < 0;
        }

        return new StockPeriodResult(
            atFrom ?? flow.Position,
            inQty,
            Round(inValue),
            outQty,
            Round(outCost),
            flow.Position,
            wentNegative);
    }

    /// <summary>Quantity and value on <paramref name="asAt"/> (inclusive).</summary>
    public static StockPosition PositionAt(ValuationMethod method, StockPosition opening, IEnumerable<StockMovement> orderedMovements, DateOnly asAt) =>
        Run(method, opening, orderedMovements, asAt, asAt).Closing;

    private static long Round(decimal value) => (long)Math.Round(value, 0, MidpointRounding.AwayFromZero);

    private interface ICostFlow
    {
        long QuantityMilli { get; }

        StockPosition Position { get; }

        /// <summary>Adds stock; returns the value booked for it.</summary>
        decimal In(long quantityMilli, long? valueMinor);

        /// <summary>Removes stock; returns its cost.</summary>
        decimal Out(long quantityMilli);
    }

    private sealed class AverageFlow : ICostFlow
    {
        private decimal _quantity;
        private decimal _value;
        private decimal _lastUnitCost;

        public AverageFlow(StockPosition opening)
        {
            _quantity = opening.QuantityMilli;
            _value = opening.ValueMinor;
            _lastUnitCost = _quantity > 0 ? _value / _quantity : 0;
        }

        public long QuantityMilli => (long)_quantity;

        public StockPosition Position => new((long)_quantity, Round(_value));

        public decimal In(long quantityMilli, long? valueMinor)
        {
            var value = valueMinor ?? (CurrentUnitCost * quantityMilli);
            _quantity += quantityMilli;
            _value += value;
            if (_quantity > 0)
            {
                _lastUnitCost = _value / _quantity;
            }
            else if (_quantity == 0)
            {
                _value = 0;
            }

            return value;
        }

        public decimal Out(long quantityMilli)
        {
            var cost = CurrentUnitCost * quantityMilli;
            _quantity -= quantityMilli;
            _value -= cost;
            if (_quantity == 0)
            {
                _value = 0;
            }

            return cost;
        }

        private decimal CurrentUnitCost => _quantity > 0 ? _value / _quantity : _lastUnitCost;
    }

    private sealed class FifoFlow : ICostFlow
    {
        private readonly LinkedList<(decimal Quantity, decimal UnitCost)> _lots = new();
        private decimal _shortfall;
        private decimal _lastUnitCost;

        public FifoFlow(StockPosition opening)
        {
            if (opening.QuantityMilli > 0)
            {
                var cost = (decimal)opening.ValueMinor / opening.QuantityMilli;
                _lots.AddLast((opening.QuantityMilli, cost));
                _lastUnitCost = cost;
            }
        }

        public long QuantityMilli => (long)(_lots.Sum(l => l.Quantity) - _shortfall);

        public StockPosition Position => new(QuantityMilli, Round(_lots.Sum(l => l.Quantity * l.UnitCost) - (_shortfall * _lastUnitCost)));

        public decimal In(long quantityMilli, long? valueMinor)
        {
            var unitCost = valueMinor is { } v ? (decimal)v / quantityMilli : CurrentUnitCost;
            decimal remaining = quantityMilli;
            if (_shortfall > 0)
            {
                var fill = Math.Min(remaining, _shortfall);
                _shortfall -= fill;
                remaining -= fill;
            }

            if (remaining > 0)
            {
                _lots.AddLast((remaining, unitCost));
            }

            _lastUnitCost = unitCost;
            return unitCost * quantityMilli;
        }

        public decimal Out(long quantityMilli)
        {
            decimal remaining = quantityMilli;
            decimal cost = 0;
            while (remaining > 0 && _lots.First is { } first)
            {
                var take = Math.Min(remaining, first.Value.Quantity);
                cost += take * first.Value.UnitCost;
                _lastUnitCost = first.Value.UnitCost;
                remaining -= take;
                if (take == first.Value.Quantity)
                {
                    _lots.RemoveFirst();
                }
                else
                {
                    first.Value = (first.Value.Quantity - take, first.Value.UnitCost);
                }
            }

            if (remaining > 0)
            {
                _shortfall += remaining;
                cost += remaining * _lastUnitCost;
            }

            return cost;
        }

        private decimal CurrentUnitCost
        {
            get
            {
                var quantity = _lots.Sum(l => l.Quantity);
                return quantity > 0 ? _lots.Sum(l => l.Quantity * l.UnitCost) / quantity : _lastUnitCost;
            }
        }
    }
}
