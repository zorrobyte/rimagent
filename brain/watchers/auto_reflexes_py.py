def watch(ctx, events):
    actions = []
    for event in events:
        kind = event.get('kind')
        if kind == 'hostile_group':
            # Draft fighters
            pawns = ctx.bridge.call("state_pawns", {"filter": "colonists"})
            for p in pawns:
                if not p.get('drafted') and "Violence" not in p.get('incapable', []):
                    actions.append({
                        "type": "action",
                        "method": "ui.draft",
                        "params": {"pawn": p['id'], "drafted": True},
                        "note": "draft for hostile group"
                    })
        elif kind == 'colonist_downed':
            # Rescue
            downed = ctx.bridge.call("state_pawns", {"filter": "downed"})
            for d in downed:
                healthy = ctx.bridge.call("state_pawns", {"filter": "healthy"})
                if healthy:
                    actions.append({
                        "type": "action",
                        "method": "ui.order",
                        "params": {"pawn": healthy[0]['id'], "at": d['id'], "label": "rescue"},
                        "note": "rescue downed colonist"
                    })
        elif kind == 'message' and "drop pod" in event.get('text', ''):
            # Unforbid drops
            pods = ctx.bridge.call("map.find", {"def": "DropPod", "forbidden": True})
            if pods:
                actions.append({
                    "type": "action",
                    "method": "ui.designate",
                    "params": {"designator": "unforbid", "things": [p['id'] for p in pods]},
                    "note": "unforbid drop pods"
                })
        elif kind == 'pawn_died':
            # Haul corpses
            corps = ctx.bridge.call("map.find", {"kind": "corpse", "forbidden": False})
            for c in corps:
                healthy = ctx.bridge.call("state_pawns", {"filter": "healthy"})
                if healthy:
                    actions.append({
                        "type": "action",
                        "method": "ui.order",
                        "params": {"pawn": healthy[0]['id'], "at": c['id'], "label": "haul"},
                        "note": "haul corpse"
                    })
    return actions
