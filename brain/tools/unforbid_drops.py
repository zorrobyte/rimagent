from rimagent.registry import tool

@tool("unforbid_drops",
      "Find forbidden items (crash-pod loot, dropped gear) within a radius of home and "
      "unforbid them so colonists will haul them. Returns how many were unforbidden and their ids.",
      {"radius": "search radius from home (default 40)"})
def unforbid_drops(ctx, radius=40):
    items = ctx.bridge.call("map.find", kind="item", forbidden=True, radius=radius, limit=200)
    if not items:
        return {"unforbidden": 0, "ids": []}
    ids = [it["id"] for it in items if it.get("id")]
    if not ids:
        return {"unforbidden": 0, "ids": []}
    res = ctx.bridge.call("ui.designate", designator="unforbid", things=ids)
    return {"unforbidden": len(ids), "ids": ids, "result": res}
