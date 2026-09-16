from rimagent.registry import tool

@tool("mood_triage",
      "One-call mood triage: for every colonist with mood below their major-break "
      "threshold, returns top negative thoughts (sorted, most negative first), the "
      "catharsis buffer if present, and the mood level. Use when mood_watch fires or "
      "you need to know which debuff to fix first without 6 separate state.pawn calls.",
      {"radius": "unused (all colonists on map)"})
def mood_triage(ctx, radius=40):
    pawns = ctx.bridge.call("state.pawns", filter="colonists")
    out = []
    for p in pawns:
        name = p.get("name")
        mood = p.get("mood")
        if mood is None:
            continue
        # Read per-pawn thresholds from the engine (3 values: minor, major, extreme)
        thresholds = None
        try:
            thresholds = ctx.bridge.call(
                "engine.get",
                path=f"Pawn:{name}.mindState.mentalBreaker",
                depth=0,
            )
        except Exception:
            pass
        # Fallback: read break_thresholds from state.pawn
        if thresholds is None:
            try:
                detail = ctx.bridge.call("state.pawn", pawn=name)
                thresholds = detail.get("break_thresholds")
            except Exception:
                thresholds = [35.0, 20.0, 5.0]

        if not isinstance(thresholds, list) or len(thresholds) < 3:
            thresholds = [35.0, 20.0, 5.0]
        minor_t, major_t, extreme_t = thresholds[0], thresholds[1], thresholds[2]

        # Only flag pawns below their major threshold
        if mood >= major_t:
            continue

        # Get thoughts
        try:
            detail = ctx.bridge.call("state.pawn", pawn=name)
            thoughts = detail.get("thoughts") or []
        except Exception:
            thoughts = []

        # Sort by mood value ascending (most negative first)
        thoughts.sort(key=lambda t: t.get("mood", 0))
        negatives = [t for t in thoughts if t.get("mood", 0) < 0]
        top_neg = negatives[:4]

        # Check for catharsis buffer (a positive thought that will fade)
        catharsis = None
        for t in thoughts:
            label = (t.get("label") or "").lower()
            thought_key = (t.get("thought") or "").lower()
            if "catharsis" in label or "catharsis" in thought_key:
                catharsis = t
                break

        out.append({
            "name": name,
            "mood": mood,
            "major_threshold": major_t,
            "extreme_threshold": extreme_t,
            "catharsis_buffer": catharsis,
            "top_negatives": [
                {"thought": t.get("thought"), "label": t.get("label"), "mood": t.get("mood")}
                for t in top_neg
            ],
        })

    out.sort(key=lambda x: x["mood"])
    return {"colony_mood_avg": None, "flagged": out, "count": len(out)}
