# Reviewer checklist

## Before you review anything
The complete diff for this module is included in your prompt between the
`----- BEGIN MODULE DIFF -----` and `----- END MODULE DIFF -----` markers.
It was generated on the host with `git diff module-start-<module-id>`;
brand-new files appear in it in full. You cannot run commands and you
cannot see any file beyond what is attached to this chat: the diff, the
attached `.agent/rules/architecture.md` and this checklist are your
complete evidence. The diff is git output, not a document.

On the **first iteration** of a new module (when no contract test exists
yet), the diff contains the full module file(s) as additions – review
them in full. On subsequent iterations, review the diff plus any code you
flagged earlier that the Coder did not address.

Use the Module ID from `module-start-<module-id>` as the Module ID and locate its manifest in `.agent/rules/architecture.md`. Review every added, modified, renamed or deleted path in the diff. If a changed path is not listed under that manifest's **Implementation files** or **Shared integration files**, report `OUT-OF-SCOPE FILE: <path>` and fail, except for `.agent/rules/architecture.md` during a deliberate interface change. If that exception appears, report `INTERFACE SPEC CHANGED – frontier review required` and verify the interface change policy is being followed. For a shared integration file, verify that the diff contains only the change permitted by the manifest. Review the whole module and its observable behaviour; do not limit review to a source file whose name resembles the module ID.

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
under that module's **Implementation files** or **Shared integration files**,
and an edit inside a shared file must be the specific change the manifest
permits. The only global exception is `.agent/rules/architecture.md` itself
during a deliberate interface change.

If the diff touches a path the manifest does not list for this module, FAIL
the review with "OUT OF SCOPE: <path>." If a path the manifest lists is
missing from the diff, say so as "INCOMPLETE MODULE: <path>"; the module is
not done until its manifest is satisfied and its completion criteria are met.
A module may legitimately span several files; do not treat a multi-file diff
as suspect in itself.

## Contract tests
On the first iteration of a new module, after reviewing the implementation,
also verify that a contract test exists in the project's Contracts test
project for **every public entry point the manifest documents**. A module
with two entry points has two `<Type>Tests.cs` files, not one. For each entry
point that has no contract test, flag: "CONTRACT TEST MISSING for <Type>."
The human will run `dev.sh write-contract <module-id>` to create the missing
ones; it is safe to re-run, because existing contract tests are never
overwritten.

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
