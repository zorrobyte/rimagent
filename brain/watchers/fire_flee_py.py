def watch(ctx, events):
    actions = []
    for event in events:
        kind = event.get('kind')
        if kind == 'incident' and 'fire' in event.get('text', ''):
            # Flee fire - since I don't have a specific flee command, I will alert the planner
            actions.append({
                "type": "alert",
                "text": "Fire detected!",
                "wake": True
            })
    return actions
