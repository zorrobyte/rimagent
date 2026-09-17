from rimagent.registry import tool
@tool(name="bulk_designate", description="Designate multiple items at once with the same action")
def bulk_designate(ctx, designator, ids):
    """
    Designates a list of item IDs.
    :param designator: The designator name (e.g., 'unforbid', 'cut', 'mine')
    :param ids: List of thing IDs
    """
    results = []
    for item_id in ids:
        res = ctx.bridge.call("ui.designate", **{"designator": designator, "things": [item_id]})
        results.append(res)
    return results
