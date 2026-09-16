def watch(ctx, events):
    """On colonist_downed: send the best available colonist to rescue the downed one.

    Picks a healthy colonist (not the downed one, not already drafted) and issues
    a rescue order. Wakes the planner so they can set up medical care.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "colonist_downed":
            continue
        downed_name = ev.get("text", "").split(" ")[0]  # "X is downed"
        downed_id = ev.get("thing")  # thing id of the downed pawn

        try:
            pawns = ctx.bridge.call("state.pawns", filter="colonists")
        except Exception:
            pawns = []

        # Pick a healthy colonist who is not the downed one
        candidates = []
        for p in pawns:
            if p.get("name") == downed_name:
                continue
            if p.get("drafted"):
                continue
            # Prefer pawns with Medical skill; fallback to any healthy pawn
            candidates.append(p)

        if not candidates:
            out.append({
                "type": "alert",
                "text": f"{downed_name} downed but no healthy colonist available to rescue",
                "wake": True,
            })
            continue

        # Sort by Medical skill (top_skills list) - higher is better
        def med_score(p):
            for s in (p.get("top_skills") or []):
                if "Medicine" in s:
                    # top_skills like "Medicine 8!!"
                    try:
                        return int(s.split()[-1].rstrip("!"))
                    except Exception:
                        return 0
            return -1

        candidates.sort(key=med_score, reverse=True)
        rescuer = candidates[0]

        # Issue rescue order: rw_ui_order(pawn=<rescuer>, at=<downed_id>, label="rescue")
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
                "Check medical policy, ensure doctor priority 1, and set up tending."
            ),
            "wake": True,
        })
    return out
