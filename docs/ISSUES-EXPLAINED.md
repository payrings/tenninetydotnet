# The Framework's Issues, Explained Simply

*For the project owner. No technical knowledge needed – everything here uses the same
picture as the beginner guide:*

> **The framework is a construction site.** You write the *blueprint* (`spec.md`). An AI
> *Architect* splits it into numbered job cards (*work packages*). Robot workers build one
> job at a time: one robot builds, a second inspects, a third runs quality checks. A site
> manager (the `tenninety` program) keeps everyone in order, and every finished job is
> permanently recorded in the project diary (*git*).

**Good news first:** the site manager, job-card system, inspection loop, undo mechanism, and
Docker role boundary are built and covered by the offline test suite. This remains an alpha with
**known weak spots and unfinished operational validation**, listed below honestly.

---

## Part 1 – Issues that deserve action soon

### Issue 0 – The safety enclosure depends on Docker operations
**Situation.** Docker mode now places builder, inspector exploration, Restore, and quality-check
commands in hardened disposable containers. The authoritative repository and Docker socket are
not mounted. The host orchestrator and Docker daemon remain trusted, and `unsafe-host` explicitly
opts out of isolation.

**Action.** Use digest-pinned role images and a patched least-privilege Docker deployment, keep
credentials out of project files, review restricted-network policy, and avoid `unsafe-host` for
untrusted generated code.

---

### Issue 1 – The real robot workers have never set foot on the site
**Situation.** Everything so far ran in *rehearsal mode*: a stand-in crew that behaves
perfectly predictably, so we could test the site rules safely. The **real** AI workers
(the local models on your computers) have never built anything here. The kitchen works;
we've simply never watched actual chefs cook in it.

**Why it's like that.** Testing with unpredictable real workers too early is like auditioning
stunts before the safety nets are up. Rehearsal first was the right order.

**What could go wrong.** Real workers might misunderstand the job cards' wording, produce
messier work than the stand-in, or get confused in ways rehearsals never showed.

**Your decision.**
- **Option A (recommended):** authorize one *supervised live trial* – a tiny blueprint,
  real workers, a throwaway copy of the site, you watching. Cheap, and it converts "should
  work" into "works".
- **Option B:** wait and rehearse longer. Safer, but the unknown stays unknown.

---

### Issue 2 – When a worker trips on a loose cable, it counts as one of his mistakes
**Situation.** Each job allows 20 failed attempts before the job is declared stuck and a
human is called. Fair enough. But right now, a failure caused by *bad luck* – say the AI
worker briefly losing connection to its brain – is counted exactly like a failure caused by
*sloppy work*. Twenty unlucky connection hiccups would get a perfectly good worker fired.

**What could go wrong.** In a long run, a flaky afternoon could stall jobs that were
actually going fine, and you'd be called to sites that didn't need you.

**Decision recorded.** The owner chose to keep the uniform attempt budget. Reviewer, tester,
and Frontier infrastructure exceptions now stop the run with resumable state instead of being
silently converted into quality failures; coder failures still consume the configured budget.

> **The full story – including a second, sneakier problem the short version leaves out
> (an absent inspector currently closes the entire site), walked-through scenarios, and
> why "just allow more attempts" is the wrong fix – is in
> [ISSUE-2-DEEP-DIVE.md](ISSUE-2-DEEP-DIVE.md).** The design for Option B there is already
> retained as the historical P0-1 design in the maintainer handover.

---

### Issue 3 – Warning labels only count if written in CAPITALS
**Situation.** When the Architect reads your blueprint and finds a contradiction, it sticks
a red warning label on the job card and refuses to let anyone build it until you decide.
But the label detector is picky: it only recognises the word written exactly as the official
manual spells it (in capitals). If a future Architect writes the same warning in lowercase,
the job slips through with no instructions – and confused robots waste attempts.

**Decision recorded.** Keep exact uppercase protocol markers until a real live plan proves
normalisation is needed. Lowercase prose remains intentionally non-operative.

---

## Part 2 – Watch-list items (decide later, but know them now)

### Issue 4 – The building inspector is very strict about floors
Jobs are organised by *floors*: foundations first, then structure, then wiring, then paint.
A job on a lower floor is never allowed to depend on one above it – sensible. But the
inspector currently **rejects the whole blueprint** for even one violation instead of
flagging it for your approval.

- Risk: occasionally a good blueprint bounces, costing you one round of corrections.
- Decision, later: if rejections become annoying in practice, ask for an *"accept anyway,
  I take responsibility"* stamp rather than loosening the inspector for everyone.

### Issue 5 – Job numbering assumes a tidy numbering habit
The site manager plays jobs in numerical order (job 2 before job 10). If the Architect ever
names jobs with words instead of numbers ("KITCHEN-01"), ordering quietly becomes less
sensible – dependencies still hold, but convenience fades.

- Decision, later: only if you see oddly ordered queues in real plans.

### Issue 6 – Editing the blueprint mid-construction raises no alarm
If you change `spec.md` while robots are working, nothing breaks – but nothing warns you
either; the change is only visible on the notice board (a small fingerprint of the blueprint
shown in status views).

- Decision: would you like a siren ("blueprint changed – pause and review?"), or is the
  quiet notice board enough? Current behaviour: quiet.

### Issue 7 – One worker at a time
Everything is deliberately built to employ **one robot at a time**. It's slow but calm –
no two robots ever fight over the same wall. The site was designed so crews can be added
later without rebuilding the site.

- Decision: none today. This is the "hire more workers later" door, kept unlocked.

---

## Part 3 – Choices already made for you (no action needed)

These come up in technical reviews, so you should know they're deliberate:

- **Complicated demolitions are refused.** If undoing a bad change requires knocking down
  walls built on top of it, the framework stops and calls *you* rather than improvising
  demolition robots. Safety over convenience – on purpose.
- **Rehearsal mode is the factory setting.** The site starts in "flight simulator" mode so
  nobody mistakes rehearsal for real construction. A notice prints at setup; switching to
  real workers is a documented settings change.
- **The site guard checks badges against a list.** Secrets (passwords, keys) are stripped
  before any document reaches an AI worker. Honest caveat: badge checks catch known forgery
  patterns, not exotic ones – treat the site as having good-not-perfect security, and don't
  feed it secrets unnecessarily.
- **All progress lives in the project diary on this computer.** The diary is robust, but it
  exists in one place. A periodic photocopy (any ordinary backup of the project folder) is
  cheap insurance no framework provides for you.

---

## Part 4 – Your decision sheet

| # | Issue | Decision needed | Recommendation |
|---|-------|-----------------|----------------|
| 0 | Docker boundary needs live operational validation | Approve a supervised sandbox trial? | **Yes, before production** |
| 1 | Real workers never tried | Approve a supervised live trial? | **Yes, next step** |
| 2 | Accidents count as mistakes | **Keep uniform budget** | Reopen only after an infra-caused BLOCKED incident |
| 3 | Capital-letter warning labels | **Keep uppercase-only** | Reopen after a real lowercase marker escapes |
| 4 | Strict floor inspector | Add owner-override stamp? | Wait for first real rejection |
| 5 | Word-based job numbering | Improve ordering? | Wait for real evidence |
| 6 | Blueprint edit alarm | Silent notice or siren? | Your preference; silent is current |
| 7 | One worker at a time | Hire crews (parallel work)? | Later – door is designed for it |
| – | Diary backup | Photocopy regularly? | **Yes, independent of everything** |

Items 2 and 3 are closed owner decisions recorded in `JUDGMENT-CALLS.md`; their previous
designs remain only as contingency notes. Item 1's supervised-live-run checklist remains in
the maintainer handover.

---

## Part 5 – Where this leaves the project

Think of the framework as a small aircraft that has passed its simulator checks but still lacks
one production safety enclosure and has not flown a real payload in real weather. The next steps
are container isolation and one supervised live trial, not unsupervised production use.
