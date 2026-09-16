def watch(ctx, events):
    """On mental_break: alert the planner immediately.

    Mental breaks are time-critical: a minor break can escalate to major/extreme
    within hours. The watcher fires the instant the event lands so the planner
    can check the break type, draft others if berserk risk, and fix the cause.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "mental_break":
            continue
        text = ev.get("text", "")
        out.append({
            "type": "alert",
            "text": (
                f"Mental break: {text} — check break type (rw_state_pawn), "
                "if berserk draft others and keep clear; if minor wait it out but fix the mood cause."
            ),
            "wake": True,
        })
    return out
