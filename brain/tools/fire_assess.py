from rimagent.registry import tool

@tool("fire_assess", "One-call fire triage: fire object count, room temps, downed colonists in fire zone, blueprint loss risk, and whether beds exist in safe temperature. Use when fire_alert fires or when assessing fire damage.", {"radius": "search radius from home (default 60)"})
def fire_assess(ctx, radius=60):
    import re
    summary = ctx.bridge.call("state.summary")
    home = summary.get("home_center", [0, 0])
    
    # Get fire objects
    fires = ctx.bridge.call("map.find", kind="item", near=home, radius=radius, limit=500)
    fire_count = 0
    for t in fires.get("things", []):
        label = t.get("label", "")
        if "Fire" in label or "fire" in label:
            fire_count += 1
    
    # Get room temperatures
    base = ctx.bridge.call("state.base")
    rooms = base.get("rooms", [])
    hot_rooms = []
    for room in rooms:
        temp = room.get("temp_c")
        if temp is not None and temp > 100:
            hot_rooms.append({"id": room.get("id"), "temp_c": temp, "label": room.get("label", "")})
    
    # Get downed colonists
    pawns = ctx.bridge.call("state.pawns", filter="colonists")
    downed = []
    mobile = []
    for p in pawns:
        if p.get("downed"):
            downed.append({
                "name": p.get("name"),
                "pos": p.get("pos"),
                "health": p.get("health"),
            })
        else:
            mobile.append(p.get("name"))
    
    # Check for beds
    beds = ctx.bridge.call("map.find", kind="building", near=home, radius=radius, limit=100)
    bed_count = sum(1 for t in beds.get("things", []) if "Bed" in t.get("def", ""))
    
    # Check blueprints
    bp_count = summary.get("blueprints", 0)
    
    result = {
        "fire_objects": fire_count,
        "hot_rooms": hot_rooms,
        "downed_colonists": downed,
        "mobile_colonists": mobile,
        "total_beds": bed_count,
        "blueprints_pending": bp_count,
        "assessment": "CRITICAL" if fire_count > 50 or (downed and hot_rooms) else
                      "ACTIVE" if fire_count > 10 else
                      "SMOLDERING" if fire_count > 0 else "CLEAR",
    }
    ctx.log(f"fire_assess: {result['assessment']}, fires={fire_count}, downed={len(downed)}, beds={bed_count}")
    return result
