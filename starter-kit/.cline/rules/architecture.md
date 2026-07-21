# [ProjectName] – System architecture and blueprint

<!--
PASTE YOUR CLOUD FRONTIER MODEL'S ARCHITECTURAL OUTPUT BELOW THIS LINE.

Ensure the pasted content defines:
- The full project structure (tree view).
- A dependency-ordered module catalogue with a unique, stable lowercase kebab-case Module ID for every module.
- A complete manifest for every module: exact implementation files, permitted shared integration files, protected contract-test path, public contract, required behaviour, required test coverage, acceptance examples, prohibited changes, and completion criteria.
- Each module's single responsibility and public interface (signatures).
- Dependency direction between modules, expressed using Module IDs.
- The full data model and the module that owns each type.
- An explicit, dependency-ordered build sequence using the same Module IDs.
- Detailed logic for any correctness-critical components.
- State ownership.
- The schema of critical_logic_golden.json.
-->

## Interface change policy
Any change to a function or class signature already defined in this
document requires:
1. Updating this document in the same diff; spec and code must never drift.
2. Mandatory frontier-model review of the change, regardless of which
   module it is in or that module's normal risk tier.
