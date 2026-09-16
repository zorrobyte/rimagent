def watch(ctx, events):
    """On hostile_group_gone: undraft all drafted colonists so they can work again.

    Drafted pawns freeze colony work (no eating, sleeping, building). After a raid
    ends, undrafting is the single most common hand-action. This watcher does it the
    moment the last hostile leaves.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "hostile_group_gone":
            continue
        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []
        drafted = [p for p in pawns if p.get("drafted")]
        for p in drafted:
            out.append({
                "type": "action",
                "method": "ui.draft",
                "params": {"pawn": p["name"], "drafted": False},
                "note": f"Undrafted {p['name']} after fight",
            })
        if drafted:
            who = ", ".join(p["name"] for p in drafted)
            out.append({
                "type": "alert",
                "text": f"Hostiles gone - undrafted {who}. Check for downed colonists and haul loot.",
                "wake": True,
            })
    return out
