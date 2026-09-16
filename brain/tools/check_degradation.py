from rimagent.registry import tool

@tool("check_degradation", "Find items degrading (HP < 90%) or rotting near the colony. Returns a list of degrading items with their HP and position.", {"radius": "search radius from home (default 30)"})
def check_degradation(ctx, radius=30):
    import re
    summary = ctx.bridge.call("state.summary")
    home = summary.get("home_center", [0, 0])
    items = ctx.bridge.call("map.find", kind="item", near=home, radius=radius, limit=200)
    degrading = []
    for t in items.get("things", []):
        label = t.get("label", "")
        m = re.search(r'\((\d+)%\)', label)
        if m:
            hp = int(m.group(1))
            if hp < 90:
                degrading.append({
                    "id": t["id"],
                    "def": t.get("def"),
                    "label": label,
                    "hp": hp,
                    "pos": t.get("pos"),
                    "count": t.get("count", 1),
                })
    outside = summary.get("outside_storage", {})
    result = {
        "degrading_count": len(degrading),
        "degrading": degrading,
        "outside_storage": outside,
        "note": "Items with HP < 90% are degrading. Move them under a roof or they will break."
    }
    return result
