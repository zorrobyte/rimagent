def watch(ctx, events):
    actions = []
    for event in events:
        # Unforbid drops
        if event.kind == 'message' and "drop pod" in event.text:
            items = ctx.bridge.call("map.find", **{"kind": "item", "forbidden": True, "limit": 50})
            if items:
                for item in items:
                    actions.append({
                        'type': 'action',
                        'method': 'ui.designate',
                        'params': {'designator': 'unforbid', 'things': [item['id']]},
                        'note': 'unforbid drop pods'
                    })
        
        # Draft fighters
        if event.kind == "hostile_group":
            pawns = ctx.bridge.call("state.pawns", **{"filter": "colonists"})
            for pawn in pawns:
                if not pawn['drafted'] and "Violence" not in pawn.get('incable', []):
                    actions.append({
                        'type': 'action',
                        'method': 'ui.draft',
                        'params': {'pawn': pawn['id'], 'drafted': True},
                        'note': 'drafted for defense'
                    })

        # Rescue downed
        if event.kind == "colonist_downed":
            downed = ctx.bridge.call("state.pawns", **{"filter": "downed"})
            if downed:
                healthy = ctx.bridge.call("state.pawns", **{"filter": "colonists"})
                for pawn in healthy:
                    if not pawn['drafted']:
                        actions.append({
                            'type': 'action',
                            'method': 'ui.order',
                            'params': {'pawn': pawn['id'], 'at': downed[0]['id'], 'label': 'rescue'},
                            'note': 'rescuing colonist'
                        })
                        break

        # Haul corpses
        if event.kind == "pawn_died":
            corpses = ctx.bridge.call("map.find", **{"kind": "corpse", "limit": 20})
            if corpses:
                pawns = ctx.bridge.call("state.pawns", **{"filter": "colonists"})
                for pawn in pawns:
                    if not pawn['drafted']:
                        for corpse in corpses:
                            actions.append({
                                'type': 'action',
                                'method': 'ui.order',
                                'params': {'pawn': pawn['id'], 'at': corpse['id'], 'label': 'haul'},
                                'note': 'hauling corpse'
                            })
                        break

    return actions
