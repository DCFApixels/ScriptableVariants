# Scriptable Variants

`ScriptableVariants` adds single-parent value inheritance and per-property overrides to
Unity `ScriptableObject` assets. Its Inspector integration is built on Tri Inspector 2.

## Requirements and installation

- Unity 6000.0 or newer.
- Tri Inspector 2 at commit `f3239650e307275edd06c25e7cda1fdc7207f5b5`.

Unity Package Manager does not support a Git package declaring another Git package as a
transitive dependency. Add both Git dependencies to the consuming project's
`Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.codewriter.triinspector": "https://github.com/codewriter-packages/Tri-Inspector.git#f3239650e307275edd06c25e7cda1fdc7207f5b5",
    "com.dcfapixels.scriptable-variants": "https://github.com/DCFApixels/ScriptableVariants.git#v0.1.1"
  }
}
```

Alternatively, add Tri Inspector first and then use **Package Manager → Add package from git
URL** with `https://github.com/DCFApixels/ScriptableVariants.git#v0.1.1`.
Authentication must already be configured for the private repository's HTTPS or SSH URL.

## Quick start

```csharp
using DCFApixels.ScriptableVariants;
using TriInspector;
using UnityEngine;

[CreateAssetMenu(menuName = "Game/Weapon Config")]
public sealed class WeaponConfig : ScriptableVariant<WeaponConfig>
{
    [SerializeField, Min(0), Slider(0, 100)]
    private float _damage = 10;

    [SerializeField]
    private WeaponVisuals _visuals;

    public float Damage
    {
        get
        {
            EnsureResolved();
            return _damage;
        }
    }
}
```

Create assets normally and assign another asset of the exact same type to **Parent** in the
native Inspector header. The same header shows the inheritance chain and the **Actions** menu.
A child reads all values from its parent. A thin blue line marks a local override; a softer blue
line on a container means that it contains overridden child fields. Locally controlled property
labels and displayed field values use bold text.

When **Parent** is assigned or changed, the asset's current effective values are compared with
the new parent's values. Every difference becomes a local override, while existing overrides
remain. Equal properties continue inheriting from the new parent; `[VariantLocal]` fields are
kept local and are not added to the override list.

Editing an inherited property automatically creates an override while preserving the rest of
the inherited data. Right-click an overridden property or its left gutter to open the variant
actions. **Apply to Parent** moves the local value to the immediate parent. **Revert** discards
the local value and restores the value from the nearest ancestor. The same menu can explicitly
create an override without changing its value. **Actions → Flatten** removes the parent while
preserving all currently effective values.

## Runtime contract

Inherited values are materialized into the child object when it is enabled and whenever
`EnsureResolved()` is called. Reflection and deep copies occur only while resolving; normal
field/property reads do not walk the parent chain.

Prefer private serialized fields and read-only public properties that call `EnsureResolved()`.
If code changes serialized values at runtime, call `InvalidateResolvedData()` afterwards.

If a derived class implements `OnEnable`, `OnDisable`, or `OnValidate`, it must override the
protected base method and call `base` so automatic invalidation remains active.

## Override boundaries

- Inline `[Serializable]` classes and structs support leaf-field overrides.
- Arrays and `List<T>` are overridden as a whole collection.
- `[SerializeReference]` values are overridden as a whole managed reference.
- Unity object references and built-in Unity values are overridden as a whole value.
- Add `[VariantLocal]` to a serialized field that must always remain local.
- Parent and child assets must have exactly the same concrete type.
- A cyclic parent chain is rejected by the Inspector and guarded against at runtime.

Override identifiers use Unity property paths. Fields renamed with `[FormerlySerializedAs]`
are remapped automatically. Unknown paths are reported in the Inspector and can be removed
with **Remove Orphans**.

## Tri Inspector

The integration wraps Tri Inspector's existing visual-element drawer chain. Tri attributes
such as groups, validation, conditionals, custom drawers, and value-change callbacks remain
responsible for rendering the actual value field.

Variant actions are added to Unity's property context menu. The blue override gutter has the
same context menu as a fallback for custom Tri Inspector controls that consume the field event.

The integration targets the pinned Tri Inspector commit above so preview API changes cannot
silently break its editor bindings.

## Sample

A ready-made three-level weapon configuration chain is available from the package details under
**Samples → Weapon Configuration Demo → Import**. See
[`Samples~/Demo/README.md`](Samples~/Demo/README.md) for its inherited and overridden fields
and a short Inspector walkthrough.
