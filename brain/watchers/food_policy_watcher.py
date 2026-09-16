def watch(ctx, events):
    """On day tick: check if ANY colonist's food policy excludes the food actually in stock.

    The #1 repeated starvation cause across episodes: survival packs are NOT in
    the "Simple" or "Raw" category. If any colonist has a policy that excludes
    survival packs while they are the only food, that colonist will starve.

    Checks ALL colonists, not just the first one (the old bug: if colonist A had
    "Lavish" which allows survival packs, the watcher would stop and miss
    colonist B's "Simple" policy which does not).
    """
    out = []
    for ev in events:
        if ev.get("kind") != "day":
            continue
        try:
            s = ctx.bridge.call("state.summary")
        except Exception:
            continue

        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []

        ks = s.get("key_stocks", {})
        survival = ks.get("MealSurvivalPack", 0) or ks.get("survival", 0)
        meals = ks.get("meals_all", 0) or ks.get("MealSimple", 0)
        raw = ks.get("raw_food", 0) or ks.get("rice", 0)

        # Which food categories are actually in stock?
        has_survival = survival > 0
        has_meals = meals > 0
        has_raw = raw > 0

        # Policies that EXCLUDE survival packs: Simple, Raw, Fine, NutrientPaste
        # Policies that ALLOW survival packs: Any, Survival, Lavish (preferability 9 < 10)
        # Lavish blocks preferability>=10; survival packs are preferability 9 → allowed
        excludes_survival = {"simple", "raw", "fine", "nutrientpaste"}

        # Check each colonist
        blocked = []
        for p in pawns:
            policy = p.get("food_policy")
            if not policy:
                # null policy = "Any" = allows everything
                continue
            pl = policy.lower()
            if pl in excludes_survival and has_survival:
                blocked.append((p.get("name", "?"), policy, survival))

        if blocked:
            names = ", ".join(f"{n} ({pol}, {n2} packs)" for n, pol, n2 in blocked)
            out.append({
                "type": "alert",
                "text": (
                    f"FOOD POLICY MISMATCH: {names} — these colonists will NOT eat "
                    f"survival packs. Set their policy to 'Any' or 'Lavish' immediately. "
                    f"food_days={s.get('food_days', '?')}."
                ),
                "wake": True,
            })

        # Also alert if no food of any kind is in stock and food_days < 3
        if not has_survival and not has_meals and not has_raw and s.get("food_days", 99) < 3:
            out.append({
                "type": "alert",
                "text": (
                    f"NO FOOD IN STOCK: food_days={s.get('food_days', '?')}, "
                    "no meals, raw food, or survival packs. Check rice harvest ETA "
                    "and queue cooking bills immediately."
                ),
                "wake": True,
            })
    return out
