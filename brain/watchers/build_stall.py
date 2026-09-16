def watch(ctx, events):
    """On day tick (day >= 1): if blueprints are queued but no frame is being
    built, construction is stalled — usually starved of materials, a forbidden
    material, or no builder with Construction enabled.

    This is the recurring 'blueprints stalling for wood' pattern. The blueprint_check
    tool does the detailed audit; this watcher just WAKES the planner early so the
    stall is fixed on the day it happens instead of a day later.

    Fires on `day` only (cheap, once per in-game day), and only when blueprints > 0
    and frames == 0. A freshly-placed blueprint legitimately has no frame for a few
    hours, so we skip day 0.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "day":
            continue
        try:
            s = ctx.bridge.call("state.summary")
        except Exception:
            continue
        day = s.get("day") or 0
        blueprints = s.get("blueprints") or 0
        frames = s.get("frames") or 0
        if day < 1 or blueprints <= 0 or frames > 0:
            continue
        out.append({
            "type": "alert",
            "text": (
                f"BUILD STALL: {blueprints} blueprint(s) queued but no frame is being "
                f"built. Run blueprint_check to see which def is starved of materials "
                f"and whether anyone has Construction enabled. Common causes: not enough "
                f"wood/steel, a forbidden material, or no builder assigned."
            ),
            "wake": True,
        })
    return out
