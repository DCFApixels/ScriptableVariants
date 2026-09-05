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
open working instances without turning inherited changes into overrides.

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
`ScriptableVariantAssetUtility.EnsureResolved(asset)`. It forces the source chain to reimport and
returns the current imported instance; normal Inspector edits and dependency imports do not need
this call.

## Serialization boundaries

- Public fields and private `[SerializeField]` fields are discovered automatically. Coverage of all
  Unity-serialized native and framework types is not yet guaranteed.
- Inline `[Serializable]` classes and structs support leaf overrides.
- Arrays and `List<T>` are overridden as one collection.
- `[SerializeReference]` values are overridden as one managed-reference graph.
- `[VariantLocal]` is supported on root and inline fields; fields inside an atomic collection or
  managed-reference value do not have independent local-only semantics.
- Unity asset references are stored by `GlobalObjectId` and registered as import dependencies.
- `AnimationCurve`, `Gradient` (including color space), and `Bounds` have dedicated value serialization.
- Fields renamed with `[FormerlySerializedAs]` are remapped during import.
- Former field names are also accepted inside inline values and collection elements; saving writes
  the current names. Date-looking strings are kept as strings, and nested collections replace
  constructor defaults instead of appending to them.
- Recursive inline type schemas are rejected with an error. Use `[SerializeReference]` for recursive
  graphs; shared managed references across separate stored fields are not currently preserved.
- Parent and child assets must have exactly the same concrete type, and cycles are rejected.

Source commands edit a detached document and replace each source file atomically after writing a
temporary sibling file. This protects an existing file from partial writes, but **Apply to Parent**
is not yet a transaction across both source files. Conflict handling for external edits and recovery
of unresolved references also remain limitations of this experimental branch. Only import trusted
`.svariant` files; polymorphic JSON types are not restricted to a security allowlist.

Regular `.asset` instances created by older package versions are not `.svariant` sources and do
not participate in the new inheritance system. Keep backups while evaluating this breaking format
on the `testing` branch.

## Sample

The package includes a three-level weapon configuration chain under **Samples → Weapon
Configuration Demo → Import**. See [`Samples~/Demo/README.md`](Samples~/Demo/README.md) for the
field-by-field walkthrough.
