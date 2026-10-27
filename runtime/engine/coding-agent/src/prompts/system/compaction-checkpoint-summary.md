You MUST summarize the conversation above into a structured handoff summary for another LLM to resume the task.

IMPORTANT: If the conversation ends with an unanswered question or a request awaiting user response (e.g., "Please run command and paste output"), you MUST preserve that exact question/request.

If a <previous-summary> block is present, it summarizes earlier history: you MUST preserve its information, merge the new messages into it, move finished "In Progress" items to "Done", and drop blockers or pending questions that were resolved.

You MUST use this format (sections can be omitted if not applicable):

## Goal
[User goals; list multiple if session covers different tasks.]

## Constraints & Preferences
- [Constraints or requirements mentioned]

## Progress

### Done
- [x] [Completed tasks/changes]

### In Progress
- [ ] [Current work]

### Blocked
- [Issues preventing progress]

## Files and Code
- [Exact path: why it matters, key changes or snippets]

## Errors and Fixes
- [Exact error: how it was resolved, plus related user feedback]

## Pending Jobs
- [Background jobs or subagents still running, with their ids; requested work not yet started]

## Current Work
[Precisely what was being worked on immediately before this summary: file, function, command, last result]

## Key Decisions
- **[Decision]**: [Brief rationale]

## Next Steps
1. [Immediate next action, directly in line with the most recent user request]
2. [Further ordered actions]

## Critical Context
- [Important data, pending questions, references]

## Additional Notes
[Anything else important not covered above]

You MUST output only the structured summary; you NEVER include extra text. You NEVER call tools.

Sections MUST be kept concise. You MUST preserve exact file paths, function names, error messages, and relevant tool outputs or command results. You MUST include repository state changes (branch, uncommitted changes) if mentioned.
