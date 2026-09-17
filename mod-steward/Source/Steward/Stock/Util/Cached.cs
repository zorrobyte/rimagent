// Replacement for ilyvion.Laboratory CachedValue<T> (MIT OR Apache-2.0); trivially re-implemented.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using Verse;

namespace RimBridge.Steward.Stock
{
    /// <summary>Tick-TTL cached value.</summary>
    public sealed class Cached<T>
    {
        private T _value;
        private int _updatedTick = -1;
        private readonly int _ttlTicks;

        public Cached(T initial, int ttlTicks) { _value = initial; _ttlTicks = ttlTicks; }

        public bool TryGetValue(out T value)
        {
            value = _value;
            if (_updatedTick < 0) return false;
            int now = Find.TickManager?.TicksGame ?? 0;
            return now - _updatedTick <= _ttlTicks;
        }

        public T Update(T value)
        {
            _value = value;
            _updatedTick = Find.TickManager?.TicksGame ?? 0;
            return value;
        }

        public void Invalidate() => _updatedTick = -1;
    }
}
