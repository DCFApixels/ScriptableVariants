# Scriptable Variants demo

The three ready-made assets in `Assets` form this chain:

`Weapon_Base` → `Weapon_Fire` → `Weapon_Fire_Boss`

Select them in this order and compare the override markers in Tri Inspector.

## What the demo shows

- `Weapon_Base` owns the family defaults.
- `Weapon_Fire` overrides its display name, one nested color field, and the entire effects list.
- `Weapon_Fire_Boss` overrides damage and nested projectile scale, while inheriting the fire color and effects from its parent and cooldown from the root.
- `Designer Note` has `[VariantLocal]`, so every asset stores it locally without an override marker.

## Things to try

1. Change `Damage` on `Weapon_Base`: both descendants update unless they override it.
2. Change `Projectile Color` on `Weapon_Fire`: the boss inherits the new color.
3. Click the circle beside `Cooldown` on the boss, then give it a local value.
4. Revert that override and confirm that the root value returns.
5. Use **Flatten** on a duplicate of the boss asset to detach it while keeping effective values.

You can create more roots through **Assets → Create → Scriptable Variants Demo → Weapon Config**
and create descendants with **Create Child** in the Inspector.
