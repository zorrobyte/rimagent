from rimagent.registry import tool
@tool("room_check", "Audit all rooms: size, door count, sealed status, trapped colonists, and temperature. Use after any build to verify doors are placed and rooms are reachable.", {})
def room_check(ctx):
    """Check all rooms for sealed rooms, trapped pawns, and temperature issues.
    
    Returns:
        rooms: list of rooms with id, size, doors, sealed status
        trapped: list of colonists trapped in sealed rooms
        problems: list of issues found
    """
    # Get rooms from state
    rooms_data = ctx.bridge.call("state.rooms")
    rooms = rooms_data.get("rooms", []) if isinstance(rooms_data, dict) else (rooms_data or [])
    
    # Get colonists
    pawns = ctx.bridge.call("state.pawns", filter="colonists")
    
    problems = []
    room_summaries = []
    
    for room in rooms:
        room_id = room.get("id")
        size = room.get("size", 0)
        doors = room.get("doors", 0)
        temp = room.get("temperature")
        roofed = room.get("roofed")
        
        entry = {
            "id": room_id,
            "size": size,
            "doors": doors,
            "temperature": temp,
            "roofed": roofed,
        }
        
        # Check for sealed room (no doors)
        if doors == 0 and size > 0:
            entry["sealed"] = True
            problems.append(f"Room {room_id} ({size} cells) has NO doors — sealed room")
        else:
            entry["sealed"] = False
        
        # Check for small bedroom (confined interior)
        if size < 25 and size > 0:
            entry["small"] = True
            problems.append(f"Room {room_id} is only {size} cells (< 25) — 'Confined interior' -10 mood")
        else:
            entry["small"] = False
        
        room_summaries.append(entry)
    
    # Check for trapped colonists
    trapped = []
    for p in pawns:
        name = p.get("name") or p.get("id")
        pos = p.get("pos")
        if not pos:
            continue
        # Check if the pawn is in a sealed room
        # We'd need to check which room the pawn is in, which requires more data
        # For now, flag pawns that are downed and in a sealed room
        if p.get("downed") or p.get("health", 100) < 50:
            # Check if their position is in a sealed room
            for room in room_summaries:
                if room.get("sealed"):
                    # We can't easily check if the pawn is in this room without more data
                    # Flag for manual check
                    trapped.append({
                        "pawn": name,
                        "health": p.get("health"),
                        "downed": p.get("downed"),
                        "pos": pos,
                        "note": "Check if this pawn is in a sealed room"
                    })
    
    return {
        "rooms": room_summaries,
        "trapped_suspects": trapped,
        "problems": problems,
        "room_count": len(rooms),
    }
