# Coding conventions and constraints

<!-- 
PASTE YOUR PROJECT-SPECIFIC CODING RULES BELOW THIS LINE.
Include:
- Naming conventions (e.g. PascalCase for public, camelCase for private).
- Where new code must be placed relative to the layout in `.cline/rules/architecture.md`.
- The approved NuGet package list (package name and version); this becomes Directory.Packages.props.
-->

## Implementation instructions
Implement the code exactly against the signatures defined in `.cline/rules/architecture.md`. Do not redesign the architecture.

For the Module ID named in the task, locate its manifest and implement the complete module. Create or edit only paths listed under that manifest's **Implementation files** or **Shared integration files**, and make only the permitted change described for a shared file. Do not infer scope from a filename in the task and do not create convenience files outside the manifest. If the task, manifest and repository disagree, stop and ask a specific clarifying question.

## Testing policy
The testing policy is in `.cline/skills/tester.md`. Read and follow it. In
particular: you cannot run tests, `dotnet build`, `dotnet test`, or any
package manager; none of them exist in your sandbox. Do not try. When a
task hands you a test failure log, treat it as ground truth and fix the
code it describes.

## Clarifying questions
If you encounter genuine ambiguity in `.cline/rules/architecture.md`, where the spec does not say what to do for a case you actually need to
handle, halt and ask the user a specific clarifying question. Do not
guess. A question costs one turn; a wrong guess costs at least three.
