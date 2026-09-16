def watch(ctx, events):
    """On day tick: if food_days < 3, alert the planner to grow/hunt/cook.

    Food crises recur (day 14: 0.4 days for 6 colonists). This watcher fires on
    each day event so the planner is woken early enough to add rice zones /
    cooking bills before starvation triggers mental breaks.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "day":
            continue
        try:
            s = ctx.bridge.call("state.summary")
        except Exception:
            continue
        food = s.get("food_days")
        if food is None:
            continue
        if food < 1:
            out.append({
                "type": "alert",
                "text": (
                    f"FOOD CRISIS: {food} days left for {s.get('colonists','?')} colonists. "
                    "Add rice growing zones now, set Growing/PlantCutting 1 on the best grower, "
                    "and queue CookMealSimple bills on the stove. Check crop_status for harvest timing."
                ),
                "wake": True,
            })
        elif food < 3:
            out.append({
                "type": "alert",
                "text": (
                    f"Food low: {food} days. Consider adding a rice zone or hunting if a harvest "
                    "isn't imminent. Check crop_status."
                ),
                "wake": True,
            })
    return out
