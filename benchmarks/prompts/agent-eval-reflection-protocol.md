# Agent Eval Reflection Protocol

Use this at the end of each benchmark run to capture explicit, auditable feedback about tool usefulness.

Do not request hidden chain-of-thought. Request concise declarative summary only.

## Required output block

Return a single JSON object with this shape:

```json
{
  "summary": "One short paragraph.",
  "helpful_tools": ["tool.id"],
  "unhelpful_tools": ["tool.id"],
  "roslyn_helpfulness_score": 1
}
```

Field guidance:

- `summary`: 1-3 sentences about what influenced success/failure.
- `helpful_tools`: tools that directly enabled progress.
- `unhelpful_tools`: tools that wasted time or produced noise.
- `roslyn_helpfulness_score`: integer 1-5 for Roslyn tools in this run.

## Prompt snippet

Use a run-end prompt like:

> Provide only the required JSON block.  
> Briefly summarize what tools helped or hurt.  
> Do not include hidden reasoning steps.  
> If no Roslyn tools were used, set `roslyn_helpfulness_score` to null.
