# Reviewer checklist

## Before you review anything
The complete diff for this module is included in your prompt between the
`----- BEGIN MODULE DIFF -----` and `----- END MODULE DIFF -----` markers.
It was generated on the host against the active baseline named in the task
(either `module-start-<module-id>` or a later repair baseline);
brand-new files appear in it in full. You cannot run commands and you
cannot see any file beyond what is attached to this chat: the diff, the
attached current module files, any human rejection feedback,
`.agent/rules/architecture.md`, and this checklist are your complete evidence.
The diff is git output, not a document.

On the **first iteration** of a new module, the diff contains every change made
since the module-start baseline and exact host-staged contract-test additions –
review them in full. A repair diff intentionally contains only changes since
the human rejection baseline. In both cases, assess completion using the
attached current module files as well as the diff; an unchanged manifest path
is not automatically missing from the completed module.

Use the Module ID from the baseline named in the task and locate its manifest in `.agent/rules/architecture.md`. Review every added, modified, renamed or deleted path in the diff. A changed path is valid when it is listed under that manifest's **Implementation files**, **Shared integration files**, or **Protected/generated test artefact**. Protected/generated test artefacts are created by the host staging workflow and mounted read-only to implementation agents; review their contract coverage, but do not report them as out of scope. The other exception is `.agent/rules/architecture.md` during a deliberate interface change. If that exception appears, report `INTERFACE SPEC CHANGED – frontier review required` and verify the interface change policy is being followed. For a shared integration file, verify that the diff contains only the change permitted by the manifest. Review the whole module and its observable behaviour; do not limit review to a source file whose name resembles the module ID.

<!--
PASTE YOUR PROJECT-SPECIFIC REVIEW CHECKLIST BELOW THIS LINE.
Include concrete, checkable items such as:
- Does every public method signature match `.agent/rules/architecture.md` exactly?
- Does any file contain raw SQL?
- Does the correctness-critical logic match the worked examples in `.agent/rules/architecture.md`?
-->

## Outside-checklist rule
If you encounter something in the diff that is not covered by the checklist
above, flag it explicitly rather than silently skipping it. Report
"OUTSIDE CHECKLIST: <what you saw>" as part of your review.

## Test-pass-by-coincidence check
When reviewing a fix for a test failure, check whether the fix addresses
the underlying bug or merely makes the test pass. Red flags:
- Broad exception catches (`catch (Exception)` or `catch`) added around
  the failing code path
- The failing assertion's condition was changed to always be true
- The test was modified (it shouldn't be, because tests are read-only for you,
  in the Contracts project, the Golden project, and tests/fixtures/)
- A mock was added to make the failing call return what the test expected

If the fix makes the test pass without addressing the bug, FAIL the review
with "TEST-PASS-BY-COINCIDENCE: <explanation>."

## Manifest scope
The module manifest in `.agent/rules/architecture.md`, located by the task's
Module ID, is the authoritative scope. Every path in the diff must appear
under that module's **Implementation files**, **Shared integration files**, or
**Protected/generated test artefact**, and an edit inside a shared file must be
the specific change the manifest permits. Protected/generated test artefacts
are valid only as host-staged test additions; review their completeness and
fail if production code attempts to replace their purpose. The only global
exception is `.agent/rules/architecture.md` itself during a deliberate
interface change.

If the diff touches a path the manifest does not list for this module, FAIL
the review with "OUT OF SCOPE: <path>." Do not require every manifest path to
appear in a repair diff: unchanged paths are represented by the attached full
files. Report "INCOMPLETE MODULE: <path>" only when that path is absent from
both the diff and the attached current files, or when its attached content does
not satisfy the manifest. A module may legitimately span several files; do not
treat a multi-file diff as suspect in itself.

## Contract tests
Verify that every exact path listed under **Protected/generated test artefact**
appears as an existing or added contract test and covers the documented public
contracts and behaviours. For each missing path, flag: "CONTRACT TEST MISSING:
<path>." The host scope gate also checks presence, and `dev.sh write-contract
<module-id>` stages the exact missing filename set without overwriting existing
tests.

## Verdict line
End every review with a single final line that is exactly one of:

    VERDICT: PASS
    VERDICT: FAIL

Put all issues and the "OUTSIDE CHECKLIST", "TEST-PASS-BY-COINCIDENCE",
"OUT OF SCOPE", "INCOMPLETE MODULE" and "CONTRACT TEST MISSING" notes on
lines above the verdict. The orchestrator requires EXACTLY ONE line in
your whole reply that begins with `VERDICT:`, and it must be exactly
`VERDICT: PASS` to pass; anything else – a `VERDICT: FAIL`, extra text on
the verdict line, more than one verdict line, or no verdict at all – is
treated as a failure. If anything failed, the verdict is FAIL.
