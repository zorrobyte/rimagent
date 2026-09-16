def watch(ctx, events):
    """On colonist_downed: send the best available colonist to rescue the downed one.

    Picks a healthy, non-drafted colonist (not the downed one), preferring the
    highest Medical skill, and issues a rescue order. Wakes the planner for
    medical setup. top_skills is a STRING like "Medicine 8!!" — parse it, don't
    iterate it as a list.

    If the rescue order fails (no bed in safe temperature, sealed room), the
    planner is woken with specific guidance.
    """
    import re
    out = []
    for ev in events:
        if ev.get("kind") != "colonist_downed":
            continue
        downed_name = (ev.get("text") or "").split(" ")[0]
        downed_id = ev.get("thing")

        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []

        def med_score(p):
            ts = p.get("top_skills") or ""
            m = re.search(r"Medicine\s+(\d+)", ts)
            return int(m.group(1)) if m else -1

        candidates = [p for p in pawns
                      if p.get("name") != downed_name and not p.get("drafted")]
        if not candidates:
            out.append({
                "type": "alert",
                "text": f"{downed_name} downed but no healthy colonist available to rescue",
                "wake": True,
            })
            continue

        candidates.sort(key=med_score, reverse=True)
        rescuer = candidates[0]

        out.append({
            "type": "action",
            "method": "ui.order",
            "params": {"pawn": rescuer["name"], "at": downed_id, "label": "rescue"},
            "note": f"{rescuer['name']} rescuing {downed_name}",
        })
        out.append({
            "type": "alert",
            "text": (
                f"{downed_name} downed - {rescuer['name']} sent to rescue. "
                "If the rescue order fails (no bed in safe temperature, or sealed room): "
                "(1) check rw_state_base for TRAPPED colonists, "
                "(2) deconstruct blocking walls if sealed, "
                "(3) place a bed blueprint in safe ground and have a builder construct it, "
                "(4) check medical policy and set Doctor priority 1."
            ),
            "wake": True,
        })
    return out
