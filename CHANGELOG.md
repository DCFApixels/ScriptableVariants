# Changelog

All notable changes to this package are documented in this file.

## Unreleased

- Fixed Assets/Create/Scriptable Variant silently doing nothing without an IMGUI event; type
  selection now uses a UI Toolkit utility window and displays creation errors in the window.
- Added format 3 with numeric arrays for Vector2/3/4, Vector2Int/3Int, Quaternion, Color and Color32,
  including nested/collection values, Bounds vectors and gradient color keys. Quaternion components
  are preserved without normalization. Only format 3 is supported; no legacy readers or migration.
- Updated the demo sources to format 3 without changing their values, overrides or asset identities.
- Attached cached typed numeric converters directly to contracts, avoiding component reflection
  and temporary numeric arrays; preserved float precision/HDR and Color32 byte values.
- Added regression cases for rejected legacy formats, component order/count, integer ranges,
  locale-independent float round trips, non-finite floats, raw quaternions, collections, Bounds and gradients.
- Added a document-wide managed-reference graph without rewriting assets during import.
- Restricted polymorphic JSON types to the loaded data schema and rejected malformed documents,
  missing references/overrides, duplicate keys, and unsupported empty native contracts.
- Added integer-vector and native-offset serialization, subasset fallback resolution and
  working-copy self-reference remapping.
- Added exact-revision conflict checks and journaled multi-source writes with rollback/recovery
  for Apply to Parent and source Undo; post-commit Editor errors no longer masquerade as failed writes.
- Retained failed editing sessions and added durable recovery snapshots, explicit retry/discard
  controls, and confirmed orphan removal that keeps other pending edits.
- Debounced source saves, bounded document caches and centralized source checks. Reused parent
  import artifacts and iterative temporary resolution instead of recursively allocating ancestors.
- Added nested Tri override markers and event-driven marker/style refresh. Routed Header/Space
  through a single decorator owner and isolated a checked Tri persistent-target adapter.
- Rejected unsupported VariantLocal declarations inside atomic values rather than ignoring them.
- Added hardening regression tests. Unity compilation, execution and performance measurements
  are still pending manual verification on this branch.
- Fixed native curve/gradient field discovery and added lossless `Bounds` value serialization.
- Preserved gradient color space and date-looking strings; nested collections now replace
  constructor defaults without appending duplicate elements during deserialization.
- Read `[FormerlySerializedAs]` aliases inside nested JSON values and collection elements.
- Added explicit recursive-inline-schema validation and cached local field paths.
- Isolated source-document JSON settings from other packages and rejected trailing JSON content.
- Made source commands edit detached documents and replace individual source files atomically.
- Registered parent source dependencies before validation can fail on missing or broken parents.
- Removed duplicate post-save snapshots, quadratic override-ancestor scans, and reflection-based
  changes to unrelated Inspector context menus.
- Added regression coverage for serialization, schema validation, source isolation, and atomic writes.
- Bumped the scripted importer revision so existing outputs use the corrected serialization after reload.
- Replaced embedded `.asset` inheritance metadata with the dedicated `.svariant` source format.
- Reduced the runtime API to the non-generic `ScriptableVariant` marker and `VariantLocalAttribute`.
- Added a scripted importer that publishes flat, concrete ScriptableObjects for runtime and
  Addressables while resolving inheritance only in the Editor.
- Changed child serialization to retain only overrides and `[VariantLocal]` values.
- Added **Assets → Create → Scriptable Variant...** for creating typed source assets.
- Kept the Tri Inspector parent header, blue override gutters, bold override styling, and property
  context actions on top of the imported objects.
- Added support for public serialized fields without requiring getters or synchronization calls.
- Added editor serializers for Unity asset references, managed references, curves, and gradients.
- Made `.svariant` imports retry automatically after their target script assembly finishes loading.
- Added a source-editing importer inspector using Tri Inspector on an editable temporary instance.
- Added source-backed Undo/Redo for field edits and Parent/Apply/Revert/Flatten actions.
- Kept working objects stable across reimports and shared between Inspectors of the same source.
- This is a breaking authoring-format change; legacy `.asset` variants are not used as parents.

## 0.1.2 - 2026-09-04

- Made non-generic `ScriptableVariant` the primary API while retaining
  `ScriptableVariant<TSelf>` as an optional typed convenience base.

## 0.1.1 - 2026-09-04

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
- Refactored override mutations and serialized path resolution to remove duplicate work.
- Fixed the **Actions** menu anchor in the Inspector header.
- Packaged the weapon configuration demo as an importable Unity Package Manager sample.

## 0.1.0 - 2026-09-04

- Added single-parent ScriptableObject inheritance.
- Added per-property and nested-field overrides.
- Added atomic collection and managed-reference overrides.
- Added `[VariantLocal]`, cycle protection, flattening, and orphan-path cleanup.
- Added Tri Inspector 2 integration.
- Added editor tests and a three-level weapon configuration demo.
