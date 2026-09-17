def tool(ctx):
    """Scans for key resources (wood, steel, food) to provide a summary."""
    resources = ["WoodLog", "Steel", "Plant_Rice", "Plant_Corn"]
    results = {}
    for res in resources:
        items = ctx.bridge.call("map.find", **{"kind": "item", "def": res, "limit": 50})
        results[res] = len(items)
    
    # Check stocks
    stocks = ctx.bridge.call("state.stocks", **{"category": "Foods"})
    
    print(f"Resource Counts: {results}")
    print(f"Food Stocks: {stocks}")
    return results
