def watch(ctx, events):
    """On day tick: check every colonist's mood against their major-break threshold.

    Fires when any colonist is below their major-break threshold (the mood level
    where a major/extreme break becomes likely within ~3 days). This is the
    pre-break window — the planner can fix the cause before the break event fires.

    The major-break threshold is a stat: pawn.mindState.mentalBreaker.BreakThresholdMajor
    which = GetStatValue(MentalBreakThreshold) * 4/7.
    Fallback: 28% (Losing is Fun base 22% + one neurotic shift).
    """
    out = []
    for ev in events:
        if ev.get("kind") != "day":
            continue
        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            continue
        for p in pawns:
            mood = p.get("mood")
            if mood is None:
                continue
            # Read per-pawn major-break threshold from the engine
            threshold = None
            try:
                t = ctx.bridge.call(
                    "engine.get",
                    path=f"Pawn:{p.get('name')}.mindState.mentalBreaker.BreakThresholdMajor",
                )
                if isinstance(t, (int, float)):
                    threshold = t
            except Exception:
                pass
            if threshold is None:
                threshold = 28  # Losing is Fun + neurotic fallback
            if mood < threshold:
                name = p.get("name")
                out.append({
                    "type": "alert",
                    "text": (
                        f"MOOD CRISIS: {name} at {mood}% (major-break threshold "
                        f"{round(threshold*100,1)}%). Run mood_triage to find the "
                        f"top negative thoughts and fix the biggest one before the break."
                    ),
                    "wake": True,
                })
    return out
