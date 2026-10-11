# 0005: Rule-driven workflow from a versioned JSON rule set

Status: accepted (Phase 2). Resolves PRD open decision 3 (rule-engine representation for Tab 1).

## Context
Tab 1 choices (configuration types, options) must decide which sections exist, which fields and attachments are required, and which
messages are raised, and none of that may be hardcoded in the client. There is no admin UI for rules (non-goal), but the rules must
be data, testable, and safe to evolve.

## Decision
- One **rule set** document (`services/workflow-service/.../RuleSets/ruleset-v1.json`) describes the tabs, the section catalogue
  (13 sections), field definitions (type, label, required, `requiredWhen`, max length), attachment policy, section rules and
  reconciliation pairs and thresholds.
- Conditions are a tiny DSL: `equals`, `in`, `isTrue`, `isEmpty`, `gt/gte/lt/lte`, `lengthLt`, `not`, `all`, `any`. A condition reads
  **facts**: Tab 1 checkbox fields (declared with `fact`, e.g. `cfg.derivatives`), `periodType`, `region`, any visible
  `section.field` value, and computed `recon.*` flags.
- Evaluation is **pure code** (`RuleEvaluator`, `ValidationEngine`, `ReconciliationCalculator`) with no I/O. Order: Tab 1 facts ->
  visibility -> reconciliation -> derived facts -> required flags and issues. One entry point, `IValidationEngine.Evaluate`.
- The rule set is stored in `RuleSets` (the embedded file is published at startup after `RuleSetValidator` passes) and each
  engagement is **pinned** to the version it was provisioned with. A new version is a new file.
- Derived data is **not stored**: tab views, issues, reconciliation rows and readiness are recomputed from field values on every
  read and save. Only the last published summary hash is stored, so `ValidationSummaryChanged` is emitted just when it moves.
- Values of hidden sections are kept but invisible to rules; they return when the section is enabled again.

## Consequences
- Adding a section or rule is a JSON change plus the catalogue integrity test (`RuleSetValidator`: unknown facts, fields, operators,
  tabs, duplicate keys, bad thresholds).
- Evaluating about 13 sections per request is cheap; measured properly in Phase 4.
- No stale issue rows: a rule-set change or a late `AttachmentAdded` cannot leave old messages behind.
- The DSL is intentionally small. Anything needing loops, arithmetic across sections or external data does not fit and would need a
  real rules engine.
- Fact-bearing fields must live in always-visible sections (checked), which avoids circular visibility.
