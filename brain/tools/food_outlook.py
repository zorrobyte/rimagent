from rimagent.registry import tool

@tool("food_outlook",
      "One-call food verdict: food_days, headcount, rice harvest ETA + nutrition, "
      "existing cooking bills (stove AND campfire), and a verdict "
      "(self-corrects / add zone / hunt). Run when food_days < 6 instead of "
      "reasoning about crops by hand.",
      {"radius": "search radius for rice (default 60)"})
def food_outlook(ctx, radius=60):
    s = ctx.bridge.call("state.summary")
    food_days = s.get("food_days")
    head = s.get("colonists") or 0
    ks = s.get("key_stocks") or {}
    meals = ks.get("meals_all", 0)
    meat = ks.get("meat_all", 0)

    # rice harvest ETA (same logic as crop_status, focused on Plant_Rice)
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

    # existing cooking bills on any stove / campfire
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
    if food_days is None:
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
        "rice": {"plants": len(plants), "avg_growth": round(avg, 3),
                 "harvestable": harvestable, "est_days_to_harvest": est_days,
                 "est_nutrition": est_nutrition},
        "cooking_bills": bills,
        "verdict": verdict,
    }
