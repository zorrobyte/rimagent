def watch(ctx, events):
    """On day tick: check if the food policy excludes the food actually in stock.

    The #1 repeated starvation cause across episodes: survival packs are NOT in
    the "Simple" or "Raw" category. If the only food is survival packs and the
    policy is "Simple" or "Raw", colonists will starve while food sits in the
    stockpile. This watcher fires on each day event and checks the policy vs
    stock, alerting the planner to fix it before food_days hits 0.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "day":
            continue
        try:
            s = ctx.bridge.call("state.summary")
        except Exception:
            continue

        # Get food policy from first colonist
        pawns = []
        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pass
        policy = None
        for p in pawns:
            if p.get("food_policy"):
                policy = p["food_policy"]
                break

        if not policy:
            continue

        # Get survival packs and meals in stock
        ks = s.get("key_stocks", {})
        survival = ks.get("MealSurvivalPack", 0) or ks.get("survival", 0)
        meals = ks.get("meals_all", 0) or ks.get("MealSimple", 0)

        pl = (policy or "").lower()
        # "Any" and "Survival" allow survival packs
        # "Simple", "Raw", "Cooked", "Fine", "NutrientPaste" do NOT
        allows_survival = pl in ("any", "survival", "")

        if survival > 0 and not allows_survival:
            out.append({
                "type": "alert",
                "text": (
                    f"FOOD POLICY MISMATCH: policy is '{policy}' but {survival} survival packs "
                    f"in stock — colonists will NOT eat them. Set policy to 'Any' or 'Survival' "
                    f"immediately. This is why food_days is {s.get('food_days', '?')}."
                ),
                "wake": True,
            })
        elif meals == 0 and survival == 0 and s.get("food_days", 99) < 3:
            out.append({
                "type": "alert",
                "text": (
                    f"NO FOOD IN STOCK: food_days={s.get('food_days', '?')}, "
                    "no meals or survival packs. Check rice harvest ETA and queue cooking bills."
                ),
                "wake": True,
            })
    return out
