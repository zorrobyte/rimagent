from rimagent.registry import tool

@tool("cook_bill",
      "Find the best cooking station (FueledStove > Campfire) and set a Forever "
      "CookMealSimple bill on it. One call instead of 3-4 separate calls. "
      "Returns the station id, bill index, and whether it was already running.",
      {"radius": "search radius from home (default 60)"})
def cook_bill(ctx, radius=60):
    # Find cooking stations
    stations = []
    for bdef in ("FueledStove", "Campfire"):
        try:
            found = ctx.bridge.call("map.find", kind="building",
                                    **{"def": bdef}, limit=10)
            for st in (found.get("things") or []):
                stations.append({"id": st.get("id"), "def": bdef, "pos": st.get("pos")})
        except Exception:
            pass

    if not stations:
        return {"error": "No cooking station found. Build a campfire (20 wood) or fueled stove first."}

    # Prefer FueledStove over Campfire
    stove = next((s for s in stations if s["def"] == "FueledStove"), None)
    campfire = next((s for s in stations if s["def"] == "Campfire"), None)
    target = stove or campfire

    if not target:
        return {"error": "No cooking station found."}

    # Check existing bills on this station
    try:
        bills = ctx.bridge.call("state.bills", thing=target["id"])
        existing = bills.get("bills") or []
    except Exception:
        existing = []

    # Check if a CookMealSimple bill is already running
    for b in existing:
        if b.get("recipe") == "CookMealSimple" and not b.get("suspended"):
            return {
                "station": target["id"],
                "def": target["def"],
                "already_running": True,
                "bill_index": b.get("index"),
                "count": b.get("count"),
                "note": "CookMealSimple already running on this station."
            }

    # Add a new Forever bill
    try:
        result = ctx.bridge.call("ui.add_bill",
                                 thing=target["id"],
                                 recipe="CookMealSimple",
                                 mode="TargetCount",
                                 count=999)
    except Exception as e:
        return {"error": f"Failed to add bill: {e}", "station": target["id"]}

    return {
        "station": target["id"],
        "def": target["def"],
        "already_running": False,
        "result": result,
        "note": "Forever CookMealSimple bill set. Make sure a colonist has Cooking priority 1-2."
    }
