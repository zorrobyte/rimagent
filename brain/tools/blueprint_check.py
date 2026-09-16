from rimagent.registry import tool

@tool("blueprint_check",
      "Audit every pending blueprint/frame: which are reachable by a builder, "
      "which defs are starved of materials, and whether anyone can build. "
      "Use when blueprints are queued but not being built.",
      {"radius": "search radius from home for blueprints (default 60)"})
def blueprint_check(ctx, radius=60):
    import json, re

    s = ctx.bridge.call("state.summary")
    home = s.get("home_center", [0, 0])
    pawns = ctx.bridge.call("state.pawns", filter="colonists")

    # 1. find blueprints (unbuilt) and frames (started)
    bp = ctx.bridge.call("map.find", kind="blueprint", near=home, radius=radius, limit=200)
    frames = s.get("frames") or 0
    bps = bp.get("things", [])

    # 2. material stocks (raw buildable stuff)
    stocks = ctx.bridge.call("state.stocks", category="ResourcesRaw", min=1)
    stock_defs = {}
    for cat in (stocks.get("categories") or [stocks]):
        for d in (cat.get("defs") or []):
            stock_defs[d.get("def")] = d.get("count", 0)
    # also grab a generic resource snapshot
    res = ctx.bridge.call("state.stocks", min=1)
    allstock = {}
    for cat in (res.get("categories") or []):
        for d in (cat.get("defs") or []):
            allstock[d.get("def")] = allstock.get(d.get("def"), 0) + d.get("count", 0)

    # 3. per-def blueprint demand (stuff cost)
    demand = {}   # def -> {"count": n, "stuff_cost": total, "work": total}
    unreachable = []
    for t in bps:
        d = t.get("def") or t.get("label")
        rec = demand.setdefault(d, {"count": 0, "stuff_cost": 0, "work": 0})
        rec["count"] += 1
        # fetch cost once per def
        if "stuff_cost" not in rec or rec.get("_cost_done") is not True:
            try:
                dd = ctx.bridge.call("defs.get", **{"def": d, "depth": 1})
                sc = dd.get("stuff_cost") or 0
                wc = dd.get("work_to_build") or 0
                rec["stuff_cost"] = sc
                rec["work"] = wc
                rec["_cost_done"] = True
            except Exception:
                rec["stuff_cost"] = 0
                rec["work"] = 0
        rec["stuff_cost"] = rec.get("stuff_cost", 0)  # per-unit; total below
        # reachability: can any colonist reach this cell?
        pos = t.get("pos")
        if pos:
            ok = False
            for p in pawns:
                try:
                    r = ctx.bridge.call("map.reachable", pawn=p["name"], target=pos)
                    if r.get("reachable"):
                        ok = True
                        break
                except Exception:
                    pass
            if not ok:
                unreachable.append({"id": t.get("id"), "def": d, "pos": pos})

    # total stuff needed per def
    for d, rec in demand.items():
        rec["total_stuff"] = rec.get("stuff_cost", 0) * rec["count"]

    # 4. who can build? read work priorities
    builders = []
    for p in pawns:
        try:
            pd = ctx.bridge.call("state.pawn", pawn=p["name"])
            wp = pd.get("work_priorities") or {}
            c = wp.get("Construction")
            if c is not None and c > 0:
                builders.append({"name": p["name"], "construction": c})
        except Exception:
            pass

    # 5. material starvation: for each def, is there enough of the most-plentiful
    #    buildable material? (approx: check WoodLog / Steel / Blocks* total)
    buildable_mat = sum(v for k, v in allstock.items()
                        if k in ("WoodLog", "Steel", "BlocksSandstone", "BlocksGranite",
                                 "BlocksLimestone", "BlocksMarble"))
    total_stuff_needed = sum(rec.get("total_stuff", 0) for rec in demand.values())
    starved = buildable_mat < total_stuff_needed

    return {
        "blueprints": len(bps),
        "frames": frames,
        "by_def": {d: {"count": r["count"], "stuff_each": r.get("stuff_cost", 0),
                        "total_stuff": r.get("total_stuff", 0)} for d, r in demand.items()},
        "buildable_materials_total": buildable_mat,
        "total_stuff_needed": total_stuff_needed,
        "material_starved": starved,
        "unreachable": unreachable,
        "unreachable_count": len(unreachable),
        "builders": builders,
        "no_builder": len(builders) == 0,
        "diagnosis": _diagnose(len(bps), frames, starved, len(unreachable), len(builders)),
    }

def _diagnose(nbp, frames, starved, n_unreach, n_builders):
    if nbp == 0:
        return "no pending blueprints"
    msgs = []
    if n_builders == 0:
        msgs.append("NO BUILDER: set Construction >0 on at least one colonist")
    if starved:
        msgs.append("MATERIAL STARVED: not enough raw material for all blueprints")
    if n_unreach:
        msgs.append(f"{n_unreach} blueprint(s) unreachable by any colonist (forbidden/area/no path)")
    if not msgs:
        msgs.append("ok - builders and materials present; should be building")
    return "; ".join(msgs)
