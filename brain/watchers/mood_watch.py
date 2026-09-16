def watch(ctx, events):
    """On day tick OR colonist_downed: check every colonist's mood against their
    major-break threshold.

    Fires when any colonist is below their major-break threshold (the mood level
    where a major/extreme break becomes likely within ~3 days). This is the
    pre-break window — the planner can fix the cause before the break event fires.

    Also fires on colonist_downed because a downed colonist at 3% mood will break
    the moment they wake up; the planner needs to know to feed/medicate them.

    The major-break threshold is a stat:
      pawn.mindState.mentalBreaker.BreakThresholdMajor
      = GetStatValue(MentalBreakThreshold) * 4/7
    Fallback: 28% (Losing is Fun base 22% + one neurotic shift).
    """
    out = []
    check_all = False
    for ev in events:
        kind = ev.get("kind")
        if kind == "day":
            check_all = True
        elif kind == "colonist_downed":
            # Only check the downed colonist (and any others already low)
            check_all = True  # check all, it's cheap

    if not check_all:
        return out

    try:
        pawns = ctx.bridge.call("state.pawns", filter="colonists")
    except Exception:
        return out

    for p in pawns:
        mood = p.get("mood")
        if mood is None:
            continue
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
