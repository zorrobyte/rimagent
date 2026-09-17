def watch(ctx, events):
    actions = []
    for event in events:
        if event.kind == "incident" and "fire" in event.text:
            # Flee fire logic
            actions.append({
                'type': 'alert',
                'text': 'Fire detected! Evacuate!',
                'wake': True
            })
    return actions
