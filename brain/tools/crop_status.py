from rimagent.registry import tool

@tool("crop_status",
      "Growth status of all growing zones. One call: per-zone avg growth, "
      "harvestable count, estimated days to full harvest, and total nutrition "
      "on the map. Use at the start of each step instead of reading plants manually.",
      {"def": "ThingDef to check (default Plant_Rice; pass empty string to check all plants in growing zones)"})
def crop_status(ctx, def_name="Plant_Rice"):
    s = ctx.bridge.call("state.summary")
    zones = s.get("zones") or []
    growing_zones = [z for z in zones if z.get("type","").startswith("growing")]
    
    # Get all plants of the given def (or all plant defs in growing zones)
    plant_defs = []
    if def_name:
        plant_defs = [def_name]
    else:
        for z in growing_zones:
            # type is "growing:Plant_Rice"
            d = z["type"].split(":",1)[1] if ":" in z.get("type","") else None
            if d and d not in plant_defs:
                plant_defs.append(d)
    
    zone_results = []
    total_nutrition = 0.0
    
    for zdef in plant_defs:
        try:
            raw = ctx.bridge.call("engine.call",
                                  path="Map.listerThings.ThingsOfDef",
                                  args=[zdef])
            plants = raw.get("result", []) if isinstance(raw, dict) else raw
        except Exception:
            plants = []
        
        if not plants:
            continue
        
        # Sample up to 20 plants for growth (engine calls are slow)
        sample = plants[:20]
        growths = []
        sown_count = 0
        for p in sample:
            pid = p.get("id","")
            try:
                g = ctx.bridge.call("engine.get", path=f"Thing:{pid}.growthInt")
                growths.append(float(g))
                s = ctx.bridge.call("engine.get", path=f"Thing:{pid}.sown")
                if s:
                    sown_count += 1
            except Exception:
                growths.append(0.0)
        
        avg_growth = sum(growths)/len(growths) if growths else 0.0
        harvestable = sum(1 for g in growths if g >= 0.95)
        # Rice: 0.30 nutrition/harvest; use 0.30 as default
        est_nutrition = len(plants) * 0.30  # rough estimate for all plants
        
        zone_results.append({
            "def": zdef,
            "total_plants": len(plants),
            "avg_growth": round(avg_growth, 3),
            "harvestable": harvestable,
            "est_days_to_harvest": round(max(0.0, (1.0 - avg_growth) * 3.0), 1),
            "est_nutrition": round(est_nutrition, 1),
        })
    
    # Food days from summary
    food_days = s.get("food_days")
    
    return {
        "day": s.get("day"),
        "food_days": food_days,
        "zones": zone_results,
        "total_est_nutrition": round(sum(z["est_nutrition"] for z in zone_results), 1),
    }
