# 10/90 .NET Developer Quickstart

This is the short path through the framework for a developer who already has the complete specification package for a small project (roughly 3–10 modules): the architecture body, the three skill files, and the golden fixture. Everything referenced here as a "Phase" points to one of the framework's two guides. Phases 0–8 are in the machine setup guide (`SETUP_GUIDE.md`); Phases 9–13 and the appendices are in the per-project guide (`WORKING_GUIDE.md`). Follow the referenced phase there, then come back. Phase numbering is continuous across the two, so every reference below is unambiguous.

## 1. Prepare the machine (one-time)

Work through the phases in order; they are sequential by design, and each one's verification step tells you whether it's safe to continue.

1. Confirm the prerequisites in Phase 0, including cloning this repository to the location defined in the guide's *Path conventions* section. Every copy step later assumes you did this.
2. Follow Phase 1 (base system and .NET SDK), Phase 2 (GPU runtime; reboot when told to), and Phase 3 (build the inference engine and verify it sees your GPU).
3. Follow Phase 4 to install and launch the model server with the tested repositories and quantisations, then run a direct completion smoke test against each model. Leave that terminal running and do everything else in a second one, as the guide instructs.
4. Follow Phase 5 (Docker plus the firewall rule; do not skip the firewall part, because it is the most common cause of "the agent can't reach the models") and Phase 6 (build both container images and check the three version numbers).
5. Follow Phase 7 to create the two Cline profiles, one per model, and run the readability check at the end of that phase.
6. Follow Phase 8 to set your OpenRouter key and chosen frontier model, and run the escalation smoke test. Even though your spec already exists, you still want escalation working before you need it mid-project.

## 2. Scaffold the project (one-time)

7. Switch to `WORKING_GUIDE.md` from here on. Follow Phase 9 end to end: create the workspace and its Git repository, then the solution and test projects (9.1), copy the manifests and generate the lockfiles (9.2), and install the orchestration scripts, hooks, and tracking files (9.3).

## 3. Install your specification (one-time)

You can skip the authoring half of Phase 10, the Appendix A prompt session, because you already hold its outputs. What remains is placement and freezing:

8. Follow Phase 10.1, but instead of running the frontier session, paste your existing architecture body, skill files, and golden fixture into the template files it has you copy. Confirm that every module has the required manifest and a unique Module ID.
9. Follow Phase 10.2 to freeze the fixture and create the read-only audit copies.
10. Follow Phase 10.3 to align the package manifest with the approved list in your coder skill file and regenerate the lockfiles.
11. Follow Phase 10.4 and make the initial commit. Nothing in the daily loop will run without it. Confirm the tree is clean before moving on.

## 4. Build the project, module by module

Take the dependency-ordered build sequence from your architecture document; that ordering is now your to-do list. For **each module, in that order**:

12. Start the module (Phase 11.1). Copy its Module ID exactly from the architecture document and keep using that same ID for every command about the module; do not invent an ID or substitute a filename.
13. Author the protected contract test (Phase 11.2). If this module owns the correctness-critical logic covered by your golden fixture, also create the golden harness in the same step, and review both files as 11.2 instructs before continuing.
14. Run the automated loop (Phase 11.3) with a one-line task naming the module. Let it cycle; it stops on success or after three failed attempts.
15. If it clears the loop: finalise, commit, and queue it (Phase 11.4), then immediately go back to step 12 for the next module. The `commit` step returns the tree to a clean state; without it, the next module cannot start. You do not need to human-review each module before starting the next; the queue exists precisely so reviewing can lag building.
16. If it fails all three attempts: escalate (Phase 11.5), starting at the plan-only tier and moving up only deliberately. After applying a fix, re-run the module's tests as that phase shows, then finalise and queue as normal.
17. If you correct the same cross-cutting mistake twice, broadcast the correction (Phase 11.6) so every later module inherits it instead of repeating it.

## 5. Review the queue on your own schedule

18. Work through the queued modules following Phase 12.2. Approve, or reject with feedback specific enough for a model to act on.
19. Feed rejections back with the fix command (Phase 12.3). Respect the hard stop there: a module rejected three times means your spec is ambiguous at that point; revise that part of the specification and re-attempt, rather than asking for a fourth fix.

## 6. Close out

20. When every module is approved, run the Phase 13 per-project verification checklist once against the finished project (the machine-level checklist at the end of the setup guide was already satisfied in step 6's era and does not repeat per project). For a small project most items were already exercised along the way; the checklist is your proof that none of the guarantees silently lapsed.
21. If anything misbehaves at any point, the troubleshooting table in Appendix C is indexed by symptom. Check it before debugging by hand.

That's the whole workflow: prepare once, scaffold once, install the spec once, then loop steps 12–17 per module while reviewing in parallel. For a small project, expect the one-time parts to take an afternoon and the per-module loop to run largely unattended.
