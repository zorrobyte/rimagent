def watch(ctx, events):
    """On fire-related events: alert the planner with a fire assessment.

    Fires are the #1 base-killer. When a fire event lands:
    1. Draft a non-drafted colonist for firefighting (if any exist).
    2. If ALL colonists are drafted, alert that they need to be undrafted to fight fire.
    3. Wake the planner with a specific alert about checking trapped colonists and blueprint loss.
    """
    out = []
    for ev in events:
        if ev.get("kind") not in ("incident", "message", "building_lost"):
            continue
        text = (ev.get("text") or "").lower()
        is_fire = ("fire" in text or "burn" in text or "blaze" in text
                   or "destroyed" in text and "wall" in text)
        if not is_fire:
            continue
        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []
        undrafted = [p for p in pawns if not p.get("drafted")]
        drafted = [p for p in pawns if p.get("drafted")]
        # Draft one undrafted colonist for firefighting
        if undrafted:
            p = undrafted[0]
            out.append({
                "type": "action",
                "method": "ui.draft",
                "params": {"pawn": p["name"], "drafted": True},
                "note": f"Drafted {p['name']} for fire response",
            })
        elif drafted:
            out.append({
                "type": "alert",
                "text": (
                    f"FIRE: all {len(drafted)} colonists are drafted — "
                    "undraft at least one to fight the fire. "
                    "Check for trapped colonists and blueprint loss in the fire zone."
                ),
                "wake": True,
            })
        out.append({
            "type": "alert",
            "text": (
                f"FIRE: {ev.get('text','')} — "
                "Check: (1) are any colonists trapped in the fire zone? "
                "(2) are beds in safe temperature for rescue? "
                "(3) are blueprints in the fire zone being destroyed? "
                "Undraft firefighters if all are drafted."
            ),
            "wake": True,
        })
    return out
