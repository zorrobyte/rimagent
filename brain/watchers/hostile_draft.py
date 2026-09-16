def watch(ctx, events):
    """On hostile_group: draft the best ranged shooters and wake the planner.

    Picks colonists by weapon (ranged first), drafts up to 2, and alerts so the
    planner can position them at the held chokepoint (door cell is colony-specific
    and lives in the notebook, so the watcher stays game-agnostic).
    """
    out = []
    for ev in events:
        if ev.get("kind") != "hostile_group":
            continue
        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []
        ranged_kw = ("rifle", "revolver", "pistol", "musket", "shotgun",
                     "bow", "crossbow", "smg", "carbine", "rifle", "machine")
        def is_ranged(w):
            w = (w or "").lower()
            return any(k in w for k in ranged_kw)
        shooters = [p for p in pawns if is_ranged(p.get("weapon"))]
        fallback = [p for p in pawns if not is_ranged(p.get("weapon"))]
        picks = (shooters + fallback)[:2]
        for p in picks:
            out.append({
                "type": "action",
                "method": "ui.draft",
                "params": {"pawn": p["name"], "drafted": True},
                "note": f"Drafted {p['name']} ({p.get('weapon')}) for hostile group",
            })
        who = ", ".join(p["name"] for p in picks) or "no colonists"
        out.append({
            "type": "alert",
            "text": f"Hostile group spotted - drafted {who}; position them at the held chokepoint and check rw_state_threats",
            "wake": True,
        })
    return out
