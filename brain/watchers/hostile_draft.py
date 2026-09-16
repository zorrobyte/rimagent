def watch(ctx, events):
    """On hostile_group: draft ALL violence-capable colonists (not just 2) and wake the planner.

    With 3+ armed colonists, every shooter should be drafted. Melee-only pawns are
    drafted last (they can still fight but are less effective). The alert tells the
    planner to position them at the chokepoint from the notebook.
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
                     "bow", "crossbow", "smg", "carbine", "machine", "flamer")
        def is_ranged(w):
            w = (w or "").lower()
            return any(k in w for k in ranged_kw)
        shooters = [p for p in pawns if is_ranged(p.get("weapon"))]
        melee = [p for p in pawns if not is_ranged(p.get("weapon"))]
        # Draft all shooters first, then melee
        picks = shooters + melee
        for p in picks:
            out.append({
                "type": "action",
                "method": "ui.draft",
                "params": {"pawn": p["name"], "drafted": True},
                "note": f"Drafted {p['name']} ({p.get('weapon') or 'melee'}) for hostile group",
            })
        who = ", ".join(p["name"] for p in picks) or "no colonists"
        out.append({
            "type": "alert",
            "text": (
                f"Hostile group spotted - drafted {who}. "
                "Position shooters at the chokepoint (door cell from notebook), "
                "melee behind them. Check rw_state_threats for count/weapons/distance. "
                "Set game speed to 1 (normal) for precise orders."
            ),
            "wake": True,
        })
    return out
