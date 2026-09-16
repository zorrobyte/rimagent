def watch(ctx, events):
    """On day tick OR colonist_downed: check every colonist's mood against their
    major-break threshold, INCLUDING the catharsis-fade crash.

    The catharsis-fade crash is the #1 off-guard pattern: after a mental break a
    colonist gets a +40 (major/extreme) or +30 (minor) catharsis buff that fades
    over ~2 days. While it's active their displayed mood looks fine (e.g. 50%)
    even though the underlying debuff (alcohol withdrawal -35, malnutrition -26)
    is still there. When the buff fades, mood crashes to (raw_mood - catharsis)
    and can cross the major threshold.

    So for each colonist we compute:
      raw_mood          = displayed mood
      catharsis_value   = the positive mood of the 'Catharsis' thought (0 if none)
      post_fade_mood    = raw_mood - catharsis_value
    and flag when raw_mood < major_threshold  OR  (catharsis present AND
    post_fade_mood < major_threshold). The second case is the pre-crash window:
    fix the cause NOW, not after the crash.

    Also fires on colonist_downed because a downed colonist at low mood will
    break the moment they wake up.
    """
    out = []
    check_all = False
    for ev in events:
        kind = ev.get("kind")
        if kind in ("day", "colonist_downed"):
            check_all = True
    if not check_all:
        return out

    try:
        pawns = ctx.bridge.call("state.pawns", filter="colonists")
    except Exception:
        return out

    for p in pawns:
        name = p.get("name")
        raw_mood = p.get("mood")
        if raw_mood is None:
            continue

        # major-break threshold (percent)
        threshold = None
        try:
            t = ctx.bridge.call(
                "engine.get",
                path=f"Pawn:{name}.mindState.mentalBreaker.BreakThresholdMajor",
            )
            if isinstance(t, (int, float)):
                threshold = t
        except Exception:
            pass
        if threshold is None:
            threshold = 28  # Losing is Fun + neurotic fallback

        # catharsis value from per-pawn thoughts
        catharsis = 0
        try:
            detail = ctx.bridge.call("state.pawn", pawn=name)
            for t in (detail.get("thoughts") or []):
                label = (t.get("label") or "").lower()
                key = (t.get("thought") or "").lower()
                if "catharsis" in label or "catharsis" in key:
                    v = t.get("mood")
                    if isinstance(v, (int, float)):
                        catharsis = max(catharsis, v)
                    break
        except Exception:
            pass

        post_fade = raw_mood - catharsis
        if raw_mood < threshold:
            out.append({
                "type": "alert",
                "text": (
                    f"MOOD CRISIS: {name} at {raw_mood}% (major threshold "
                    f"{round(threshold*100,1)}%). Run mood_triage and fix the "
                    f"biggest negative thought now."
                ),
                "wake": True,
            })
        elif catharsis > 0 and post_fade < threshold:
            out.append({
                "type": "alert",
                "text": (
                    f"CATHARSIS CRASH IMMINENT: {name} looks fine at {raw_mood}% "
                    f"but has +{catharsis:.0f} catharsis that will fade -> post-fade "
                    f"~{post_fade:.0f}% (< major threshold {round(threshold*100,1)}%). "
                    f"Fix the underlying debuff NOW (alcohol withdrawal? malnutrition? "
                    f"confined interior?) before the crash. Run mood_triage."
                ),
                "wake": True,
            })
    return out
