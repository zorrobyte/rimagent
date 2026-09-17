def watch(ctx, events):
    actions = []
    for event in events:
        if event.kind == 'message' and "drop pods" in event.text:
            actions.append({
                'type': 'action',
                'method': 'ui.unforbid',
                'params': {},
                'note': 'unforbid drop pods'
            })
        if event.kind == 'incident' and "fire" in event.text:
            actions.append({
                'type': 'alert',
                'text': 'Fire detected! Flee or fight.',
                'wake': True
            })
    return actions
