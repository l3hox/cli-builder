# Development Process

How this project is developed — a repeatable methodology for agent-orchestrated software engineering.

## Methodology

### Phase 1: Specification
Write the spec first. Define the problem, core concept, interfaces, metadata models, and scope boundary before any code exists. The spec is the canonical reference for *what* we're building.

### Phase 2: Architectural decisions
Each significant decision gets an ADR with context, alternatives considered, rationale, and consequences. Decisions are made through structured research — concrete data (downloads, stars, release dates, license compatibility), not gut feeling.

### Phase 3: Multi-agent council review
The spec undergoes structured review by a panel of specialist agents (Developer Council from [forge-council](https://github.com/n4m3z/forge-council)). The council runs 3-round debates:

1. **Round 1 — Initial findings.** Each specialist (SoftwareDeveloper, QaTester, DocumentationWriter, SecurityArchitect) reviews independently from their perspective.
2. **Round 2 — Cross-challenge.** Specialists respond to each other's findings by name. Dev's gap compounds QA's gap. Security's concern overrides Docs' suggestion.
3. **Round 3 — Convergence.** Specialists state what they agree on, where they disagree, and their final prioritized recommendations.

The lead (human) synthesizes a verdict with prioritized actions. Council findings that are architectural go to ADRs. Behavioral details go to design notes. Implementation gaps go to agent build plans.

### Phase 4: Design notes
Council findings and edge-case policies are captured in `docs/design-notes.md` — the middle layer between high-level spec and implementation plans. This ensures behavioral rules are specified before code is written.

### Phase 5: Implementation planning
Step-by-step agent build plans go in `docs/internal/`. Each plan specifies:
- What the agent needs to know (context)
- What it can decide autonomously (scope)
- Where it must stop for human review (checkpoints)
- Acceptance criteria (how to verify the output)

### Phase 6: TDD implementation
Build step by step. Tests first at each boundary, then implementation until tests pass. Purpose-built test fixtures for deterministic, fast tests. Golden-file snapshots for generator output stability.

### Phase 7: Validation
End-to-end validation against real SDKs (OpenAI, Stripe). Integration tests with actual API calls (optional, account-gated).

## Documentation structure

```
cli-builder-spec.md        ← What (interfaces, models, requirements, scope)
docs/ADR.md                ← Why (architectural decisions with rationale)
docs/design-notes.md       ← How edge cases behave (council findings, behavioral rules)
docs/internal/             ← Agent build plans (step-by-step implementation instructions)
docs/process.md            ← This file — how we work
FUTURE.md                  ← Parking lot for deferred ideas
AGENTS.md                  ← Quick-start for agents and contributors
README.md                  ← Public-facing overview
```

**Single source of truth rule:** Each piece of information exists in exactly one place. The spec defines interfaces. ADRs explain why. Design notes specify edge cases. Implementation plans say how to build it. No duplication across layers.

## Tools and acknowledgements

- **[forge-council](https://github.com/n4m3z/forge-council)** — Multi-agent council framework used for structured spec review and architectural debate. Provides specialist agent definitions (SoftwareDeveloper, QaTester, DocumentationWriter, SecurityArchitect) and the 3-round debate protocol.
- **Claude Code** — AI coding assistant used for implementation, research, and agent orchestration.
