from rimagent.registry import tool
import re

@tool("colony_health",
      "One-call triage: food, mood, research, threats, downed, letters, alerts, "
      "stalled blueprints. Returns a compact dict with a `triage` list of the most "
      "urgent items first. Run at the start of a step instead of 6 separate reads.",
      {"day": "min day to start caring about raid timing (default 0)"})
def colony_health(ctx, day=0):
    s = ctx.bridge.call("state.summary")
    alerts = s.get("alerts") or []
    letters = s.get("pending_letters") or []
    threats = s.get("threat_points")
    food = s.get("food_days")
    mood = s.get("mood_avg")
    downed = s.get("downed") or []
    research = s.get("research_current")
    rprog = s.get("research_progress")
    blueprints = s.get("blueprints")
    frames = s.get("frames")
    hostiles = s.get("hostiles") or []
    designations = s.get("designations")

    triage = []

    # 1. downed colonists are the top emergency
    if downed:
        triage.append({"urgency": 0, "issue": f"DOWED: {len(downed)} colonist(s) downed - rescue/doctor now"})
    # 2. letters that need a choice
    if letters:
        triage.append({"urgency": 1, "issue": f"{len(letters)} unanswered letter(s): {letters}"})
    # 3. hostiles on the map
    if hostiles:
        triage.append({"urgency": 1, "issue": f"{len(hostiles)} hostile(s) on map"})
    # 4. food
    if food is not None and food < 3:
        triage.append({"urgency": 2, "issue": f"FOOD CRISIS: {food} days left"})
    elif food is not None and food < 6:
        triage.append({"urgency": 3, "issue": f"food low: {food} days - grow/hunt"})
    # 5. mood
    if mood is not None and mood < 35:
        triage.append({"urgency": 2, "issue": f"MOOD LOW: {mood}% - mental break risk"})
    # 6. stalled construction: blueprints queued but no frames being built
    if blueprints and not frames:
        triage.append({"urgency": 3, "issue": f"{blueprints} blueprint(s) with no frame - check materials/forbid/builder"})
    # 7. alerts
    if alerts:
        triage.append({"urgency": 4, "issue": f"alerts: {alerts}"})

    triage.sort(key=lambda t: t["urgency"])

    return {
        "day": s.get("day"), "hour": s.get("hour"), "season": s.get("season"),
        "food_days": food, "mood_avg": mood, "threat_points": threats,
        "research": research, "research_progress": rprog,
        "blueprints": blueprints, "frames": frames,
        "downed": downed, "hostiles": len(hostiles),
        "designations": designations,
        "triage": triage,
    }
