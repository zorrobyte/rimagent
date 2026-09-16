def watch(ctx, events):
    """On hostile_group where the text mentions 'rat': alert specifically about the
    recurring manhunter-rat threat. Rats keep coming back; after each kill, check
    if more are on the map and consider permanent defense (wall off approach,
    rat trap). Also fires on colonist_downed where the text mentions 'rat' so
    the planner knows the cause before triaging.
    """
    out = []
    for ev in events:
        kind = ev.get("kind")
        text = (ev.get("text") or "").lower()
        if kind == "hostile_group" and "rat" in text:
            out.append({
                "type": "alert",
                "text": (
                    "MANHUNTER RAT spotted. Rats are a recurring threat — after the fight, "
                    "check rw_state_threats for more rats on the map. Consider walling off "
                    "the approach lane or building a rat trap. Draft a shooter with a revolver "
                    "and position them at the door."
                ),
                "wake": True,
            })
        elif kind == "colonist_downed" and "rat" in text:
            out.append({
                "type": "alert",
                "text": (
                    "Colonist downed by rat. Check if the rat is still alive "
                    "(rw_state_threats) and draft a shooter to kill it before it "
                    "attacks again. Set the downed colonist's medical policy to "
                    "NormalOrWorse so they get industrial medicine."
                ),
                "wake": True,
            })
    return out
