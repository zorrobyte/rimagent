Episode reflection. The game ended after {days} days. Reason: {reason}

This is the long reflection between games. The next game starts from the brain as you leave it now, so the only things that matter are edits to skills, tools, watchers and the journal. Colony-specific details die with this episode.

## Tips from the human operator this run (make sure each one is reflected in a skill)
{operator}

## Timeline of the episode (condensed)

{timeline}

## Final notebook

{notebook}

## Journal (recent)

{journal}

## Skills

{skills_index}

## Score history (this episode is the last row)

{scores}

## What to do, in order

1. Post-mortem in 3-6 lines of visible text: the cause of the end (or the cap), the day of each major loss, and the earliest step at which it could have been prevented.
2. Compare this episode's score with previous honest episodes in the score history. If a brain change made things worse, find it (`brain_log`, `brain_diff`) and `brain_revert` it, and say why in the journal.
3. Make the fixes, each concrete and checked against the timeline:
   - `skill_write` edits to the strategy skills whose advice was wrong or too vague (put the numbers in: first raid day and size, how many days food lasted, which research paid off, what killed a colonist); edit `core-doctrine` or `bridge-manual` only for rules that were clearly missing or wrong, and check `score_history` first.
   - `watcher_write` for every reflex you did by hand more than twice this game.
   - `tool_write` for every repeated multi-call routine (day-1 setup, room-with-door-and-beds, count food days from stocks, raid positioning).
   - delete or merge skills that duplicate each other (`skill_delete`).
4. `journal_append` one to three durable lessons: mechanics you verified, tool quirks, orderings that worked. No colony-specific facts.
5. Call `end_turn` with a one-paragraph note on what the next game should do differently.

Keep visible text short; put the work in the tool calls.
