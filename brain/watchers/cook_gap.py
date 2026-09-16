def watch(ctx, events):
    """On day tick: if no CookMealSimple bill is running and food is in stock and
    food_days < 6, automatically set a Forever CookMealSimple bill on the best
    cooking station (FueledStove > Campfire). Also alerts the planner.

    This is the #1 repeated failure: rice harvests but nobody cooked it because
    the bill was never set (or got suspended/duplicate-cleaned). Fires on day
    events only.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "day":
            continue
        try:
            s = ctx.bridge.call("state.summary")
        except Exception:
            continue

        food_days = s.get("food_days")
        if food_days is None or food_days >= 6:
            continue

        # Check cooking bills on all stations
        has_bill = False
        target_station = None
        for bdef in ("FueledStove", "Campfire"):
            try:
                found = ctx.bridge.call("map.find", kind="building",
                                        **{"def": bdef}, limit=5)
                for st in (found.get("things") or []):
                    try:
                        bills = ctx.bridge.call("state.bills", thing=st.get("id"))
                        for b in (bills.get("bills") or []):
                            if b.get("recipe") == "CookMealSimple" and not b.get("suspended"):
                                has_bill = True
                    except Exception:
                        pass
                    if has_bill:
                        break
            except Exception:
                pass
            if has_bill:
                break

        if has_bill:
            continue

        # No bill running — find a station to set one on
        for bdef in ("FueledStove", "Campfire"):
            try:
                found = ctx.bridge.call("map.find", kind="building",
                                        **{"def": bdef}, limit=5)
                for st in (found.get("things") or []):
                    target_station = st.get("id")
                    break
            except Exception:
                pass
            if target_station:
                break

        if not target_station:
            # No cooking station at all
            out.append({
                "type": "alert",
                "text": (
                    f"NO COOKING STATION: food_days={food_days}, no campfire or stove found. "
                    "Build a campfire (20 wood) immediately."
                ),
                "wake": True,
            })
            continue

        # Set the bill
        try:
            ctx.bridge.call("ui.add_bill",
                            thing=target_station,
                            recipe="CookMealSimple",
                            mode="TargetCount",
                            count=999)
            out.append({
                "type": "alert",
                "text": (
                    f"COOK BILL AUTO-SET: food_days={food_days}, no bill was running. "
                    f"Set Forever CookMealSimple on {target_station}. "
                    "Make sure a colonist has Cooking priority 1-2."
                ),
                "wake": True,
            })
        except Exception as e:
            out.append({
                "type": "alert",
                "text": (
                    f"COOK BILL FAILED: food_days={food_days}, tried to set bill on "
                    f"{target_station} but got: {e}. Run cook_bill tool manually."
                ),
                "wake": True,
            })
    return out
