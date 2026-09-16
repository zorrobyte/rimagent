def watch(ctx, events):
    """On fire-related events: alert the planner and draft a firefighter.

    Fires are a recurring threat (day 12 fire at campfire). When a fire event
    lands, draft the best available colonist and wake the planner so they can
    position a firefighter and check for spreading.
    """
    out = []
    for ev in events:
        if ev.get("kind") not in ("incident", "message"):
            continue
        text = (ev.get("text") or "").lower()
        if "fire" not in text and "burn" not in text and "blaze" not in text:
            continue
        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []
        # Pick a non-drafted colonist to draft for firefighting
        picks = [p for p in pawns if not p.get("drafted")]
        for p in picks[:1]:
            out.append({
                "type": "action",
                "method": "ui.draft",
                "params": {"pawn": p["name"], "drafted": True},
                "note": f"Drafted {p['name']} for fire response",
            })
        out.append({
            "type": "alert",
            "text": f"FIRE: {ev.get('text','')} — draft a firefighter, check for spreading, and verify no colonists are trapped.",
            "wake": True,
        })
    return out
