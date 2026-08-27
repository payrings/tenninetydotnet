# Authoring `spec.md` – The Recommended Pipeline

**Audience:** business analyst + developer (the two humans behind the blueprint).
This page describes how a project idea becomes the single `spec.md` file that
`tenninety plan` sends to the Frontier Architect together with the embedded blueprint prompt.

The recommended pipeline has four stages:

```text
Business need identified ──▶ Business analysis ──▶ Specification via OpenSpec ──▶ spec.md ──▶ tenninety plan
     (business owner)            (analyst)          (BA + developer)              (consolidated)
```

Commands below are identical in **bash** and **fish**.

---

## Stage 1 – Business need identified *(business owner)*

One paragraph, no tools. What problem exists, who suffers from it, what "solved" looks like.
If you cannot write this paragraph, stop here – everything downstream inherits the fuzziness.

## Stage 2 – Business analysis *(analyst)*

Turn the need into structure before any tooling: actors and roles, scope boundaries
(what is explicitly *out*), constraints (budget, compliance, existing systems), and
measurable success criteria. Output: short analysis notes that will seed OpenSpec proposals.

## Stage 3 – Specification via OpenSpec *(BA + developer together)*

[OpenSpec](https://openspec.dev/) is a lightweight, open-source planning layer. Requirements
live as plain Markdown in your repository (`openspec/specs/<capability>/spec.md`) and every
proposed change produces reviewable artefacts before any code is written.

Prerequisites: Node.js ≥ 20.19, then:

```bash
npm install -g @fission-ai/openspec@latest
cd your-project
openspec init        # prints the exact slash-command form for your AI coding agent
```

OpenSpec drives your existing AI coding assistant via slash commands (30+ tools supported,
including Qwen Code):

| Command | Use it when |
| --- | --- |
| `/opsx:explore` | The idea is still fuzzy – think it through with no artefacts committed |
| `/opsx:propose <capability>` | Requirements are clear enough – generates `proposal.md`, `specs/` deltas, `design.md`, `tasks.md` under `openspec/changes/<id>/` |
| `/opsx:apply`, `/opsx:archive` | Later – implementation bookkeeping once 10/90 robots build the feature |

Division of labor while iterating on proposals:

- **Business analyst** owns the requirements: each capability's `spec.md` entries written as
  testable *SHALL* statements with concrete **GIVEN / WHEN / THEN** scenarios.
- **Developer** owns `design.md`: technical approach, chosen stack/libraries, API shapes,
  data model sketches. This is also where "standard industry assumption" choices live.
- Review together; refine proposals until requirements stop moving. Stable truths accumulate
  under `openspec/specs/`.

## Stage 4 – Consolidate into `spec.md`

10/90 consumes **one file**: a `spec.md` with exactly three sections (shape reference:
[`../samples/spec.md`](../samples/spec.md)). Map your OpenSpec output like this:

| OpenSpec artefact | Goes into |
| --- | --- |
| `openspec/specs/*/spec.md` – SHALL requirements + scenarios | `## Business Rules` |
| `openspec/changes/*/design.md` – technical decisions, stack, libraries | `## Technical Hints` |
| UI-capability specs (screens, components) | `## UI Descriptions` |

Keep the `openspec/` folder in the repository – it remains the living requirements library;
10/90 itself only ever reads `spec.md`.

### Path A – manual consolidation

Walk the mapping table yourself: one bullet per SHALL requirement under Business Rules,
scenarios indented beneath their requirement, design decisions condensed into Technical
Hints, screens described in UI Descriptions. Slowest, but every word is yours.

Example fragment (TaskManager sample):

```markdown
## Business Rules
- The system SHALL let users switch task status TODO → IN_PROGRESS → IN_REVIEW → DONE.
  - WHEN the assignee transitions a task THEN the transition is recorded with timestamp.
```

### Path B – AI-assisted draft, human-approved

Paste this into your coding agent (it can read `openspec/` directly), then review:

```text
Read every file under openspec/ (stable specs and all changes).
Consolidate them into a single spec.md for our planner with exactly three sections:
## Business Rules  – every SHALL requirement as one bullet; attach key scenarios as sub-bullets
## Technical Hints – tech stack, libraries, API shapes, and decisions taken from design.md files
## UI Descriptions – screens and components from any UI-related capability specs
Rules: do NOT invent requirements absent from openspec/. Write "ASSUMPTION: ..." in front of
anything ambiguous instead of deciding silently. Keep total length under ~2 pages.
```

**Mandatory review checklist after Path B:**
1. Requirement count matches OpenSpec (no silent drops).
2. Stack and versions match the `design.md` files.
3. No credentials or secrets copied in.
4. Each assumption is one you actually accept – delete or replace the rest.
5. Deliberate omissions are fine: the Architect records gaps as assumptions or flags jobs
   AMBIGUOUS/CONFLICT rather than inventing silently.

## After the spec is ready

Run the standard entry point – the blueprint prompt is embedded in the framework and is sent
automatically along with your sanitised `spec.md`:

```bash
tenninety plan --spec ./spec.md --yes
tenninety status      # review the generated execution graph
tenninety start       # begin autonomous construction
```

## Mid-project changes

New needs do not restart the pipeline. Pair the two mechanisms:

1. Run a new OpenSpec **change proposal** for the altered capability (requirements stay honest).
2. Once consolidated into `spec.md`, run a 10/90 **pivot** (`[S] Snapshot & Pivot`): the
   Frontier reclassifies work packages KEEP / REWORK / CANCEL against the updated spec.

---

References: [OpenSpec documentation](https://github.com/Fission-AI/OpenSpec/blob/main/docs/getting-started.md) ·
sample target shape [`../samples/spec.md`](../samples/spec.md) · the blueprint prompt that
receives your spec is embedded in `src/Tenninety.Frontier/Prompts/Prompts.cs`.
