from rimagent.registry import tool

@tool("refugee_intake",
      "One-call triage when new colonists join: unforbid their gear, list all "
      "colonists with top skills + drug needs (the -35 withdrawal break risk), "
      "check beds vs headcount, check food days. Run immediately after a "
      "colonist_joined event.",
      {"radius": "search radius for forbidden items (default 40)"})
def refugee_intake(ctx, radius=40):
    s = ctx.bridge.call("state.summary")
    pawns = ctx.bridge.call("state.pawns", filter="colonists")

    # 1. Unforbid forbidden items near home
    try:
        items = ctx.bridge.call("map.find", kind="item", forbidden=True,
                                radius=radius, limit=200)
    except Exception:
        items = []
    ids = [it["id"] for it in (items or []) if it.get("id")]
    unforbidden = 0
    if ids:
        try:
            ctx.bridge.call("ui.designate", designator="unforbid", things=ids)
            unforbidden = len(ids)
        except Exception:
            pass

    # 2. Colonist roster with top skills + drug-need flag
    roster = []
    for p in pawns:
        name = p.get("name")
        rec = {
            "name": name,
            "id": p.get("id"),
            "top_skills": p.get("top_skills", []),
            "weapon": p.get("weapon"),
            "mood": p.get("mood"),
            "pos": p.get("pos"),
        }
        # Best-effort drug-need read: a deficit drug need = withdrawal risk (-35)
        drug_need = None
        try:
            needs = ctx.bridge.call("engine.get", path=f"Pawn:{name}.needs")
            for n in (needs or []):
                dn = str(n.get("defName", ""))
                if "Drug" in dn or "Alcohol" in dn:
                    lvl = n.get("CurLevel")
                    drug_need = {"need": dn, "level": lvl}
                    break
        except Exception:
            pass
        rec["drug_need"] = drug_need
        roster.append(rec)

    # 3. Beds vs colonists
    beds = ctx.bridge.call("map.find", kind="building", **{"def": "Bed"}, limit=100)
    bed_count = len(beds.get("things", [])) if beds else 0
    colonist_count = len(pawns)

    # 4. Food
    food_days = s.get("food_days")

    # 5. Growing zones
    zones = s.get("zones") or []
    growing = [z for z in zones if "growing" in str(z.get("type", "")).lower()]

    # 6. Who has a drug need (the break risk)
    drug_pawns = [r["name"] for r in roster if r.get("drug_need")]

    note_bits = [f"{colonist_count} colonists, {bed_count} beds, "
                 f"food {food_days} days, {len(growing)} growing zones."]
    if food_days is not None and food_days < 3:
        note_bits.append("FOOD CRITICAL - add rice zones and cooking bills now.")
    if drug_pawns:
        note_bits.append(
            f"DRUG NEEDS: {', '.join(drug_pawns)} - bank that drug or they will "
            "hit -35 withdrawal and break.")

    return {
        "day": s.get("day"),
        "colonists": colonist_count,
        "roster": roster,
        "drug_need_pawns": drug_pawns,
        "beds": bed_count,
        "beds_sufficient": bed_count >= colonist_count,
        "food_days": food_days,
        "food_critical": food_days is not None and food_days < 3,
        "growing_zones": len(growing),
        "unforbidden_items": unforbidden,
        "forbidden_ids": ids,
        "note": " ".join(note_bits),
    }
