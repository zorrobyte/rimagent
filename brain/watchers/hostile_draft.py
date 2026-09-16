def watch(ctx, events):
    """On hostile_group event, draft Gamble and Sock and alert planner."""
    out = []
    for ev in events:
        if ev.get("kind") == "hostile_group":
            # Draft the two best shooters
            for pawn in ["Gamble", "Sock"]:
                out.append({
                    "type": "action",
                    "method": "ui.draft",
                    "params": {"pawn": pawn, "drafted": True},
                    "note": f"Drafted {pawn} for hostile group"
                })
            out.append({
                "type": "alert",
                "text": "Hostile group spotted - fighters drafted, check threats and position at door [149,131]",
                "wake": True
            })
    return out
