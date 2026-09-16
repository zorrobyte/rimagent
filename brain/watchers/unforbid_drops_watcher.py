def watch(ctx, events):
    """On a drop-pod / crash-landed message: unforbid the dropped loot so it gets hauled.

    Crash-landed and drop-pod items start forbidden; nobody hauls them until unforbidden.
    Fires on `message`/`incident` events whose text mentions a drop/crash/pod/landing.
    """
    out = []
    kw = ("drop", "crash", "pod", "landing", "landed", "supply")
    for ev in events:
        if ev.get("kind") not in ("message", "incident"):
            continue
        text = (ev.get("text") or "").lower()
        if not any(k in text for k in kw):
            continue
        try:
            items = ctx.bridge.call("map.find", kind="item", forbidden=True, radius=40, limit=200)
        except Exception:
            items = []
        # items may be a list of dicts with "id" key, or a list of strings (ids), or {"things": [...]}
        ids = []
        if isinstance(items, dict):
            raw = items.get("things") or items.get("items") or []
            for it in raw:
                if isinstance(it, dict):
                    if it.get("id"):
                        ids.append(it["id"])
                elif isinstance(it, str):
                    ids.append(it)
        elif isinstance(items, list):
            for it in items:
                if isinstance(it, dict):
                    if it.get("id"):
                        ids.append(it["id"])
                elif isinstance(it, str):
                    ids.append(it)
        if ids:
            try:
                ctx.bridge.call("ui.designate", designator="unforbid", things=ids)
            except Exception:
                pass
            out.append({
                "type": "action",
                "method": "ui.designate",
                "params": {"designator": "unforbid", "things": ids},
                "note": f"Unforbidden {len(ids)} dropped item(s)",
            })
        out.append({
            "type": "alert",
            "text": f"Drop/crash event - {len(ids)} forbidden item(s) unforbidden; haul them to a stockpile.",
            "wake": True,
        })
    return out
