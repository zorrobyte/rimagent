from rimagent.registry import tool
@tool("designate_forbidden", "Finds forbidden items of a specific type and designates them.")
def designate_forbidden(ctx, item_def, near=[0,0], radius=20, designator="unforbid"):
    """
    Finds forbidden items of a specific type and designates them.
    Replaces repeated rw_map_find and rw_ui_designate calls.
    
    :param item_def: The ThingDef (e.g., 'Steel', 'WoodLog').
    :param near: [x, z] center for search.
    :param radius: Search radius.
    :param designator: The designator label (e.g., 'haul', 'unforbid', 'mine').
    """
    items = ctx.bridge.call("map.find", {"def": item_def, "near": near, "radius": radius, "forbidden": True})
    if not items:
        return f"No forbidden {item_def} found in range."
    
    ids = [i['id'] for i in items]
    ctx.bridge.call("ui.designate", {"designator": designator, "things": ids})
    return f"Designated {len(ids)} forbidden {item_def} items."

@tool("audit_pawns", "Audits health, mood, and critical needs for multiple pawns.")
def audit_pawns(ctx, names):
    """
    Audits health, mood, and critical needs for multiple pawns in one go.
    Replaces repeated rw_state_pawn calls.
    
    :param names: List of colonist names.
    """
    audit = []
    for name in names:
        try:
            pawn = ctx.bridge.call("state_pawn", {"pawn": name})
            audit.append({
                "name": name,
                "mood": pawn['needs']['mood']['CurLevel'],
                "food": pawn['needs']['food']['CurLevel'],
                "joy": pawn['needs']['joy']['CurLevel'],
                "hediffs": pawn['health']['hediffSet']
            })
        except Exception as e:
            audit.append({"name": name, "error": str(e)})
    return audit
