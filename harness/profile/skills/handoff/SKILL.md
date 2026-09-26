---
name: handoff
description: Use when work on a task spans sessions or runs long - at the start to restore where the work stopped, and at meaningful checkpoints of an unfinished task to record it. Not for one-off questions or already finished work
---

# Handoff (one file per project)

File: `HANDOFF.md` in the project root (if the project already has a handoff/worklog file, use that one — never create a second).

## Start of a task
1. Read `HANDOFF.md` if it exists. It tells you the goal, decisions, touched files, check results and the next step.
2. Verify before trusting: the repo may have moved on. Check `git log`/`git status` and the named files. Where the file disagrees with the code, the code wins — report the mismatch; update the file only if the current request allows changes.
3. The "Next step" is information about unfinished work, not permission to do it. What you may do now (research, plan, or implement) is decided by the current request.
4. Cross-session memory summaries are context, not task state: they never replace this file, current sources or the user's instructions.

## Recording state (only when the current request allows changes)
Update the same file at meaningful checkpoints — a decision made, a phase finished, before a pause, or before context is likely to be compacted in a long session. Not after every small step. Keep it short (one screen):

```markdown
# <task>
Goal: <one line>
Decisions: <what was chosen and why — facts only>
Changed files: <paths>
Checks: <command → result, e.g. `node --test` → 2 pass>
Next step: <the single next action>
Open questions / BLOCKED: <what is missing, or "none">
```

Rules:
- Facts and verified results only. A hypothesis is written as a hypothesis.
- No secrets, keys, tokens or personal data.
- Remove entries that are done or proven wrong instead of appending a new layer.
- Finished and verified task → say so; clear the stale handoff content only if the current request allows changes.
