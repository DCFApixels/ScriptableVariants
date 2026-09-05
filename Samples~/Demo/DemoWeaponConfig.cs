using System;
using System.Collections.Generic;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Demo
{
    [Serializable]
    public sealed class DemoWeaponVisuals
    {
        [SerializeField]
        private Color _projectileColor = Color.white;

        [SerializeField, Min(0.1f)]
        private float _projectileScale = 1f;

        [SerializeField]
        private Vector3 _muzzleOffset = new Vector3(0f, 0f, 0.75f);

        public Color ProjectileColor => _projectileColor;
        public float ProjectileScale => _projectileScale;
        public Vector3 MuzzleOffset => _muzzleOffset;
    }

    public sealed class DemoWeaponConfig : ScriptableVariant
    {
        [Header("Identity")]
        [SerializeField]
        private string _displayName = "Weapon";

        [Header("Combat")]
        [SerializeField, Min(0f)]
        private float _damage = 10f;

        [SerializeField, Min(0.01f)]
        private float _cooldown = 0.5f;

        [Header("Presentation")]
        [SerializeField]
        private DemoWeaponVisuals _visuals = new DemoWeaponVisuals();

        [SerializeField]
        private List<string> _effects = new List<string>();

        [Header("Always local")]
        [SerializeField, VariantLocal, TextArea]
        private string _designerNote;

        public string DisplayName => _displayName;
        public float Damage => _damage;
        public float Cooldown => _cooldown;
        public DemoWeaponVisuals Visuals => _visuals;
        public IReadOnlyList<string> Effects => _effects;

        public string DesignerNote => _designerNote;
    }
}
