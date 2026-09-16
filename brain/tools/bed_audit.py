from rimagent.registry import tool

@tool("bed_audit",
      "One-call bed triage: every bed with its owner setting (Anyone/Colonist/"
      "Prisoner/Specific), medical flag, and assigned owner; plus headcount and "
      "how many beds are actually usable by colonists. Use when the 'not enough "
      "beds' alert fires or before assigning beds to new colonists.",
      {"radius": "search radius from home (default 40)"})
def bed_audit(ctx, radius=40):
    s = ctx.bridge.call("state.summary")
    head = s.get("colonists") or 0
    found = ctx.bridge.call("map.find", kind="building",
                            **{"def": "Bed"}, limit=40)
    beds = []
    colonist_usable = 0
    for b in (found.get("things") or []):
        bid = b.get("id")
        rec = {"id": bid, "pos": b.get("pos")}
        try:
            rec["owner_setting"] = ctx.bridge.call(
                "engine.get", path=f"Thing:{bid}.bedOwnerSetting")
        except Exception:
            rec["owner_setting"] = "?"
        try:
            rec["medical"] = ctx.bridge.call(
                "engine.get", path=f"Thing:{bid}.medical")
        except Exception:
            rec["medical"] = False
        try:
            o = ctx.bridge.call("engine.get",
                                path=f"Thing:{bid}.bedOwner")
            rec["owner"] = (o.get("name") or o.get("defName")) if isinstance(o, dict) else o
        except Exception:
            rec["owner"] = None
        beds.append(rec)
        # usable by a colonist if setting allows colonists and not locked to a
        # specific pawn who is not in the colony
        os_ = str(rec.get("owner_setting", ""))
        if "Colonist" in os_ or "Anyone" in os_:
            colonist_usable += 1
        elif "Specific" in os_ or "Prisoner" in os_:
            pass
        else:
            colonist_usable += 1  # unknown, count optimistically
    return {
        "day": s.get("day"),
        "colonists": head,
        "total_beds": len(beds),
        "colonist_usable": colonist_usable,
        "shortfall": max(0, head - colonist_usable),
        "beds": beds,
        "note": ("OK" if colonist_usable >= head
                 else f"SHORT {max(0, head - colonist_usable)} bed(s) for colonists - "
                      "build more or switch a Prisoner/Specific bed to Colonist/Anyone"),
    }
