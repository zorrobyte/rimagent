def watch(ctx, events):
    """On letter events: detect raid/threat letters and alert the planner immediately.

    Raid letters are the single most time-critical event in the game. The planner
    gets woken by the default wake_on list, but this watcher adds specific guidance
    based on the letter type so the response is faster and more targeted.

    Also fires on quest letters (accept/reject decisions matter).
    """
    out = []
    for ev in events:
        if ev.get("kind") != "letter":
            continue
        text = (ev.get("text") or "").lower()
        if "raid" in text or "attack" in text or "war" in text:
            out.append({
                "type": "alert",
                "text": (
                    f"RAID LETTER: {ev.get('text','')} — "
                    "Check rw_state_threats NOW for count/weapons/distance. "
                    "Draft all shooters, position at chokepoint (door cell from notebook). "
                    "Set game speed to 1. If the raid is far away (>50 cells), "
                    "use the time to check/repair walls and add sandbags/spike traps."
                ),
                "wake": True,
            })
        elif "quest" in text or "caravan" in text or "trade" in text:
            out.append({
                "type": "alert",
                "text": (
                    f"QUEST/TRADE LETTER: {ev.get('text','')} — "
                    "Read rw_state_letters for choices. Accept trade caravans early "
                    "(steel, cloth, components). Reject quests that send pawns far from base."
                ),
                "wake": True,
            })
        elif "refugee" in text or "arriving" in text:
            out.append({
                "type": "alert",
                "text": (
                    f"REFUGEE LETTER: {ev.get('text','')} — "
                    "Check if they have an addiction (drug need = -35 mood if no drug in stock). "
                    "Check beds vs headcount. Run refugee_intake tool when they arrive."
                ),
                "wake": True,
            })
    return out
