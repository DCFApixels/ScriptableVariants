# Changelog

All notable changes to this package are documented in this file.

## Unreleased

- Moved the parent selector, inheritance chain, and actions into Unity's native Inspector header.
- Assigning or changing **Parent** now keeps existing overrides and automatically overrides
  every serialized property whose current value differs from the new parent.
- Replaced the header action-button row with a compact **Actions** menu and removed
  **Create Child** from the Inspector.
- Replaced override buttons with compact Unity-style blue gutter bars.
- Added property and gutter context actions for overriding, applying to the parent, and reverting.
- Editing an inherited property now creates its override automatically.
- Override bars now align with the actual property row below Tri Inspector decorators.
- Locally controlled property labels and field values are displayed in bold.
- Prevented Unity `Header` and `Space` decorators, including those on `[VariantLocal]` fields,
  from being drawn twice when another attribute makes Tri Inspector use Unity's native property
  handler.

## 0.1.0 - 2026-09-04

- Added single-parent ScriptableObject inheritance.
- Added per-property and nested-field overrides.
- Added atomic collection and managed-reference overrides.
- Added `[VariantLocal]`, cycle protection, flattening, and orphan-path cleanup.
- Added Tri Inspector 2 integration.
- Added editor tests and a three-level weapon configuration demo.
