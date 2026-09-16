from rimagent.registry import tool

@tool("food_outlook",
      "One-call food verdict: food_days, headcount, rice harvest ETA + nutrition, "
      "existing cooking bills (stove AND campfire), food policy check, and a verdict "
      "(self-corrects / add zone / hunt / fix policy). Run when food_days < 6 instead of "
      "reasoning about crops by hand.",
      {"radius": "search radius for rice (default 60)"})
def food_outlook(ctx, radius=60):
    s = ctx.bridge.call("state.summary")
    food_days = s.get("food_days")
    head = s.get("colonists") or 0
    ks = s.get("key_stocks") or {}
    meals = ks.get("meals_all", 0)
    meat = ks.get("meat_all", 0)

    # Check food policy vs stock
    policy = None
    pawns = ctx.bridge.call("state.pawns", filter="colonists")
    for p in (pawns or []):
        if p.get("food_policy"):
            policy = p["food_policy"]
            break
    # Check if survival packs are in stock
    survival = ks.get("MealSurvivalPack", 0) or ks.get("survival", 0)
    policy_mismatch = False
    policy_warning = None
    if policy and survival > 0:
        pl = (policy or "").lower()
        # "Any" allows everything; "Survival" allows survival packs
        # "Simple", "Raw", "Cooked", "Fine" do NOT allow survival packs
        if pl not in ("any", "survival", ""):
            policy_mismatch = True
            policy_warning = (f"Food policy is '{policy}' but {survival} survival packs in stock "
                             f"— colonists will NOT eat them. Set policy to 'Any' or 'Survival'.")

    # rice harvest ETA
    plants = []
    try:
        raw = ctx.bridge.call("engine.call",
                              path="Map.listerThings.ThingsOfDef", args=["Plant_Rice"])
        plants = raw.get("result", []) if isinstance(raw, dict) else (raw or [])
    except Exception:
        plants = []
    growths = []
    for p in plants[:30]:
        try:
            g = ctx.bridge.call("engine.get", path=f"Thing:{p.get('id')}.growthInt")
            growths.append(float(g))
        except Exception:
            pass
    avg = (sum(growths) / len(growths)) if growths else 0.0
    harvestable = sum(1 for g in growths if g >= 0.95)
    est_days = round(max(0.0, (1.0 - avg) * 3.0), 1)
    est_nutrition = round(len(plants) * 0.30, 1)

    # existing cooking bills
    bills = []
    for bdef in ("FueledStove", "Campfire"):
        try:
            found = ctx.bridge.call("map.find", kind="building",
                                    **{"def": bdef}, limit=10)
            for st in (found.get("things") or []):
                sid = st.get("id")
                b = ctx.bridge.call("state.bills", thing=sid)
                for bill in (b.get("bills") or []):
                    bills.append({"stove": sid, "recipe": bill.get("recipe"),
                                  "count": bill.get("count"),
                                  "suspended": bill.get("suspended")})
        except Exception:
            pass

    # Verdict
    if policy_mismatch:
        verdict = (f"POLICY MISMATCH: {policy_warning} "
                   f"Fix the policy BEFORE anything else — this is why food_days is {food_days}.")
    elif food_days is None:
        verdict = "unknown"
    elif food_days >= 6:
        verdict = "OK - no action"
    elif est_days <= food_days + 0.5:
        verdict = (f"SELF-CORRECTS: rice harvest in {est_days}d "
                   f"(+{est_nutrition} nutrition) before starvation. "
                   "BUT only if BOTH hold: (1) a grower has Growing/PlantCutting 1, "
                   "(2) a cook bill is running on a stove/campfire. "
                   "Check cooking_bills below - if empty, queue CookMealSimple now.")
    else:
        verdict = (f"ADD CAPACITY NOW: harvest in {est_days}d but food runs out in "
                   f"{food_days}d - add a rice zone OR designate a hunt, and queue cooking bills")

    return {
        "day": s.get("day"),
        "food_days": food_days,
        "colonists": head,
        "meals_in_storage": meals,
        "meat_in_storage": meat,
        "survival_packs": survival,
        "food_policy": policy,
        "policy_mismatch": policy_mismatch,
        "policy_warning": policy_warning,
        "rice": {"plants": len(plants), "avg_growth": round(avg, 3),
                 "harvestable": harvestable, "est_days_to_harvest": est_days,
                 "est_nutrition": est_nutrition},
        "cooking_bills": bills,
        "verdict": verdict,
    }
