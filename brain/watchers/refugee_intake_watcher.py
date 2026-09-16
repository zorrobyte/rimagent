def watch(ctx, events):
    """On colonist_joined: unforbid their gear and alert the planner to set work priorities.

    New colonists (refugees, crash-landed) arrive with forbidden gear.
    This watcher unforbids it and wakes the planner to assign work priorities.
    """
    out = []
    for ev in events:
        if ev.get("kind") != "colonist_joined":
            continue
        # Unforbid any forbidden items near the new colonist's position
        try:
            items = ctx.bridge.call("map.find", kind="item", forbidden=True, radius=40, limit=200)
        except Exception:
            items = []
        ids = [it["id"] for it in (items or []) if it.get("id")]
        if ids:
            try:
                ctx.bridge.call("ui.designate", designator="unforbid", things=ids)
            except Exception:
                pass
            out.append({
                "type": "action",
                "method": "ui.designate",
                "params": {"designator": "unforbid", "things": ids},
                "note": f"Unforbidden {len(ids)} item(s) for new colonist",
            })
        out.append({
            "type": "alert",
            "text": (
                f"New colonist joined: {ev.get('text','')} — "
                f"{len(ids)} forbidden item(s) unforbidden. "
                "Set work priorities (rw_ui_set_work) and check beds/food for the new headcount."
            ),
            "wake": True,
        })
    return out
