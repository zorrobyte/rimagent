from rimagent.registry import tool
@tool("food_policy_check", "Check if the current food policy allows the food actually in stock. Returns the policy, the food types in stock, and whether they match.", {})
def food_policy_check(ctx):
    """Check if the food policy matches the food in stock.
    
    Returns:
        policy: the current food policy string
        food_in_stock: list of food thing defs in the stockpile
        match: True if the policy allows the food in stock
        warning: string if there's a mismatch
    """
    summary = ctx.bridge.call("state.summary")
    
    # Get food policy from the first colonist (or any colonist)
    pawns = ctx.bridge.call("state.pawns", filter="colonists")
    policy = None
    if pawns:
        for p in pawns:
            if p.get("food_policy"):
                policy = p["food_policy"]
                break
        if policy is None:
            # Try to get it from the pawn's policies
            for p in pawns:
                name = p.get("name") or p.get("id")
                if name:
                    try:
                        pawn_state = ctx.bridge.call("state.pawn", pawn=name)
                        if pawn_state and "food_policy" in pawn_state:
                            policy = pawn_state["food_policy"]
                            break
                    except Exception:
                        pass
    
    # Get food stocks
    stocks = summary.get("key_stocks", {})
    food_items = []
    for k, v in stocks.items():
        if v and isinstance(v, int) and v > 0:
            # Check if this is a food item
            if any(x in k.lower() for x in ["meal", "food", "rice", "potato", "corn", "meat", "berry", "survival", "egg", "milk", "nutrient"]):
                food_items.append({"def": k, "count": v})
    
    # Also check outside storage
    outside = summary.get("outside_storage", {})
    for k, v in outside.items():
        if v and isinstance(v, int) and v > 0:
            if any(x in k.lower() for x in ["meal", "food", "rice", "potato", "corn", "meat", "berry", "survival", "egg", "milk", "nutrient"]):
                food_items.append({"def": k, "count": v, "location": "outside"})
    
    # Determine if policy matches
    # "Any" allows everything
    # "Survival" allows survival packs
    # "Simple" allows simple meals (NOT survival packs)
    # "Raw" allows raw food (NOT survival packs, NOT meals)
    # "Cooked" allows cooked meals
    # "Fine" allows fine meals
    # "NutrientPaste" allows nutrient paste
    
    policy_lower = (policy or "Any").lower()
    
    # Map policy to allowed food categories
    allowed = set()
    if "any" in policy_lower:
        allowed = {"any"}
    elif "survival" in policy_lower:
        allowed = {"survival"}
    elif "simple" in policy_lower:
        allowed = {"simple", "raw"}
    elif "raw" in policy_lower:
        allowed = {"raw"}
    elif "cooked" in policy_lower:
        allowed = {"simple", "cooked"}
    elif "fine" in policy_lower:
        allowed = {"fine", "cooked", "simple"}
    elif "nutrient" in policy_lower:
        allowed = {"nutrient"}
    else:
        allowed = {"any"}  # unknown policy, assume allows all
    
    # Check each food item
    warnings = []
    for item in food_items:
        d = item["def"].lower()
        if "any" in allowed:
            continue
        if "survival" in d:
            if "survival" not in allowed and "any" not in allowed:
                warnings.append(f"Survival pack in stock but policy '{policy}' does not allow it")
        elif "meal" in d or "simple" in d or "cooked" in d:
            if "simple" not in allowed and "cooked" not in allowed and "fine" not in allowed:
                warnings.append(f"{item['def']} in stock but policy '{policy}' may not allow it")
        elif "raw" in d or "rice" in d or "potato" in d or "corn" in d or "meat" in d or "berry" in d or "egg" in d:
            if "raw" not in allowed and "any" not in allowed:
                warnings.append(f"{item['def']} in stock but policy '{policy}' does not allow raw food")
    
    return {
        "policy": policy,
        "food_in_stock": food_items,
        "match": len(warnings) == 0,
        "warnings": warnings,
    }
