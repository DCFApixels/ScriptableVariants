# Scriptable Variants

[English](README.md) | [Русский](README.ru.md)

`ScriptableVariants` adds single-parent inheritance and per-property overrides to Unity
`ScriptableObject` data. Authoring uses a compact `.svariant` source file; Unity imports that
source as a normal, concrete and fully resolved `ScriptableObject`.

The Inspector integration is built for Tri Inspector 2. Tri remains responsible for the actual
field UI, attributes, groups, validation and callbacks. Scriptable Variants adds the parent header,
override markers and context actions around those controls.

## Requirements and installation

- Unity 6000.0 or newer.
- Tri Inspector 2 at commit `f3239650e307275edd06c25e7cda1fdc7207f5b5`.
- Newtonsoft Json for Unity 3.0.2 (declared as a package dependency).

Unity Package Manager does not support a Git package declaring another Git package as a
transitive dependency. Add Tri Inspector first, then add Scriptable Variants from its Git URL:

```json
{
  "dependencies": {
    "com.codewriter.triinspector": "https://github.com/codewriter-packages/Tri-Inspector.git#f3239650e307275edd06c25e7cda1fdc7207f5b5",
    "com.dcfapixels.scriptable-variants": "https://github.com/DCFApixels/ScriptableVariants.git"
  }
}
```

Authentication must already be configured for the private repository's HTTPS or SSH URL.

## Define a variant type

`ScriptableVariant` is intentionally only a marker base class. Serialized data may use either
public fields or private `[SerializeField]` fields; no property wrappers or synchronization calls
are required.

```csharp
using DCFApixels.ScriptableVariants;
using TriInspector;
using UnityEngine;

public sealed class WeaponConfig : ScriptableVariant
{
    [Min(0), Slider(0, 100)]
    public float Damage = 10;

    [SerializeField]
    private WeaponVisuals _visuals;

    public WeaponVisuals Visuals => _visuals;
}
```

Create the source through **Assets → Create → Scriptable Variant...** and choose its concrete
type. Do not use `[CreateAssetMenu]` for variant types: Unity's built-in command creates a regular
`.asset`, which has no variant source metadata.

## Inspector workflow

Select the `.svariant` source to edit it. Its importer inspector draws an editable temporary
instance through Tri Inspector and automatically writes changes to the source file. The generated
runtime object is not edited directly. Undo/Redo restores both values and inheritance metadata,
including after closing the Inspector; Apply to Parent restores the parent and child together.
Multiple Inspectors of the same source share one working instance. A dependency reimport refreshes
clean working instances without turning inherited changes into overrides. Dirty instances are
retained when a source or parent changes externally; the Inspector reports the conflict instead
of overwriting either version.

Typing is saved after a short debounce (250 ms), or when an action runs / the Inspector closes.
On failure, **Retry Save** retries the pending edits; **Reload from Source** requires confirmation
before discarding them. Unknown stored fields block normal saves and remain in the source until
you explicitly choose **Remove Orphan Data...** or restore their script declarations.

Assign another `.svariant` of the exact same concrete type to **Parent**. The header shows the
inheritance chain and an **Actions** menu. A thin blue line marks a local override; a softer blue
line on a container means that it contains overridden children. Locally controlled labels and
displayed field values are bold.

When a parent is first assigned or replaced, current child values are compared with the new
parent. Every difference becomes an override and existing overrides remain. Equal fields inherit
from the new parent. `[VariantLocal]` fields always remain local and never get override markers.

Editing an inherited field creates an override. Right-click the field or its blue gutter to use
**Override Property**, **Apply to Parent**, or **Revert**. **Actions → Flatten** removes the parent
while retaining all effective values.

## File and runtime model

A root `.svariant` stores all root field values. A child stores only:

- its parent asset GUID;
- its override property paths and their values;
- values marked `[VariantLocal]`.

The scripted importer resolves the parent first, applies the stored overrides, and publishes a
concrete `ScriptableObject` as the file's main asset. Parent assets are registered as import
dependencies, so changing a parent causes its descendants to be reimported. Edits are debounced
briefly before reimport to keep ordinary Inspector typing responsive.

The imported object is flat. Player code, asset references and Addressables load the concrete
type and read its fields directly; the player has no parent graph, override list, reflection pass,
`EnsureResolved`, or synchronization API. Inheritance exists only while Unity imports and edits
the source asset.

Editor tooling that needs an immediate refresh can call
`ScriptableVariantAssetUtility.EnsureResolved(asset)`. It requests immediate imports of the source chain
without `ForceUpdate` and
returns the current imported instance; normal Inspector edits and dependency imports do not need
this call.

## Serialization boundaries

- Public fields and private `[SerializeField]` fields are discovered automatically. Coverage of all
  Unity-serialized native and framework types is not yet guaranteed.
- Inline `[Serializable]` classes and structs support leaf overrides.
- Arrays and `List<T>` are overridden as one collection.
- `[SerializeReference]` values are overridden as one managed-reference graph.
- `[VariantLocal]` is supported on root and inline fields; fields inside an atomic collection or
  managed-reference value do not have independent local-only semantics. Such declarations now
  produce an explicit error; mark the owning collection/reference `[VariantLocal]` instead.
- Unity asset references are stored by `GlobalObjectId` and registered as import dependencies.
  Missing, malformed or incompatible references fail resolution instead of silently becoming null.
  An explicit JSON `null` remains a valid null reference. Scene/transient references cannot be saved.
- `AnimationCurve`, `Gradient` (including color space), `Bounds`, `Hash128`, integer vectors and
  `RectOffset` have dedicated value serialization. Native types with no supported field contract
  are rejected rather than written as empty objects.
- `Vector2`, `Vector3`, `Vector4`, `Vector2Int`, `Vector3Int`, `Quaternion`, `Color` and `Color32` are written as
  numeric JSON arrays, for example `[1.12, 2.01, 0.0012]` or `[3.5, 0.25, 0, 1]` (RGBA).
  Cached, typed converters read/write components directly without reflection or temporary numeric
  arrays. `Color` retains floating-point/HDR values; `Color32` stores integer bytes in `0..255`.
  `Quaternion` stores raw `[x, y, z, w]` components without normalization. Arrays require the exact component
  count; fractional/out-of-range integers are errors, not rounded or clamped values. Non-finite
  floats retain Json.NET's explicit `"NaN"`, `"Infinity"` and `"-Infinity"` strings.
  The same converters apply inside collections, `Bounds`, and gradient color keys. Gradients
  store each color key as `{"color": [r, g, b, a], "time": t}`. Previous named-component objects
  and flat gradient color keys are not supported.
- Fields renamed with `[FormerlySerializedAs]` are remapped during import.
- Former field names are also accepted inside inline values and collection elements; saving writes
  the current names. Date-looking strings are kept as strings, and nested collections replace
  constructor defaults instead of appending to them.
- Recursive inline type schemas are rejected with an error. Use `[SerializeReference]` for recursive
  graphs. Shared objects and cycles are preserved across values stored in the same document.
  A child override is a detached graph: it does not share managed identity with a parent's stored
  graph or an independently inherited field. Put data requiring that identity under one atomic owner.
- Parent and child assets must have exactly the same concrete type, and cycles are rejected.

Only **formatVersion 3** is supported: numeric vector/quaternion/color arrays and one managed-reference
scope per document. Earlier versions are rejected; no migration or legacy-reading code is provided.
The demo sources have been updated to this format. Importing a source never rewrites it.
Limits are 32 MiB per source, 128 JSON nesting levels and 512 assets per parent chain. Duplicate JSON
keys/value paths, unknown document members, invalid versions and unresolved `$ref` entries are errors.

## Save safety and recovery

Source commands edit detached documents and compare the exact source revisions before writing,
including the parent chain used by an open working copy. Conflicts leave pending edits untouched.
Each source is replaced atomically using a flushed temporary sibling file. **Apply to Parent** and
source Undo use a journaled batch: all targets are preflighted, ordinary failures roll back both
files and working values, and imports are queued only after the batch finishes. This is recoverable
multi-file writing, not an operating-system atomic transaction across files or external processes.

Interrupted batches are checked at Editor startup and before the next write. Journals live under
`Library/ScriptableVariants/Transactions`. If a third-party revision prevents recovery, the journal
is retained and further writes stop: reconcile the before/after/source versions before removing a
resolved journal. Do not remove `Library/ScriptableVariants` while it contains needed recovery data.

Failed closed Inspectors retain their working copy. Pending values are also backed up under
`Library/ScriptableVariants/Recovery` before assembly reload / normal Editor exit, and after failed
saves or closing a dirty Inspector. Reopening the source restores that snapshot without accepting
external changes as its baseline. Unreadable snapshots are retained for manual recovery. Backups are
not a guarantee against a crash before the debounce/backup runs. Transient references (including an
unsaved working-copy self-reference) cannot be durably backed up; fix/save them before a domain reload.
Normal Editor exit is refused if pending edits cannot be backed up.

Polymorphic JSON metadata can resolve only loaded types reachable from the declared data schema
and its serializable derived types; it cannot request an arbitrary assembly load. This is not a
sandbox for project-defined constructors or callbacks. Import only trusted `.svariant` files.

## Editor integration and remaining boundaries

Nested inline fields receive override markers too. Marker/font refresh uses session and layout
events instead of a per-field filesystem timer. Source caches are bounded; source checks and
debounced saves are shared per open asset. Actual imports reuse the parent's imported artifact;
temporary resolution reuses an output object rather than constructing every ancestor recursively.
Large dependency fan-outs still require descendant imports, and saving still snapshots the stored
graph. These changes do not establish performance guarantees for very large graphs.

Unity `Header`/`Space` decorators have one owner: the native property handler when Tri chooses it,
otherwise the integration's Tri decorator. No private wrapper names are used. To keep Tri object
pickers asset-only on a temporary SO, a small checked adapter sets Tri's public `TargetIsPersistent`
property through its protected setter. That bridge is version-sensitive; unsupported Tri versions
report an error. Native object fields also disallow scene objects; custom third-party drawers remain
responsible for their own persistent-target assumptions.

Unity calls `OnEnable` when temporary ScriptableObjects are created, before variant values have
been applied. Do not derive serialized state from inherited values in that callback. Native Unity
serialization coverage and custom drawers/callbacks still require project-specific tests; unsupported
types should use explicit surrogate data. The hardening changes have syntax/static checks and new
regression tests, but have not yet been compiled or executed in Unity on this branch.

Regular `.asset` instances created by older package versions are not `.svariant` sources and do
not participate in the new inheritance system. Keep backups while evaluating this breaking format
on the experimental branches.

## Sample

The package includes a three-level weapon configuration chain under **Samples → Weapon
Configuration Demo → Import**. See [`Samples~/Demo/README.md`](Samples~/Demo/README.md) for the
field-by-field walkthrough.
