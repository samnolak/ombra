<system-reminder reason="step_checkpoint" calls="{{count}}">
Step checkpoint: {{count}} tool calls since the last user message. Long runs without a check-in drift from the task and fill the context.

Stop calling tools now. Report in a few lines:
1. Done: what you finished; name the files you changed.
2. Left: what remains of the task.
3. Blocking: what stops you or is unclear; write "nothing" if nothing does.

{{#if subagent}}
Then call `yield` with this report as your result; your parent decides whether to continue.
{{else}}
Then ask the user whether to continue and end your turn. NEVER resume work before the user answers.
{{/if}}

This is a checkpoint, not a failure: NEVER undo work or invent a blocker because of it.
</system-reminder>
