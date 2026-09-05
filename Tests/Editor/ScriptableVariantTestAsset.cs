using System;
using System.Collections.Generic;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class ScriptableVariantTestAsset : ScriptableVariant
    {
        public int PublicNumber;
        public AnimationCurve Curve = new AnimationCurve();
        public Gradient Gradient = new Gradient();
        public Bounds Bounds;

        [SerializeField]
        private ScriptableVariantTestNestedData _nested = new ScriptableVariantTestNestedData();

        [SerializeField]
        private List<int> _values = new List<int>();

        [SerializeField, VariantLocal]
        private string _localNote = "local default";

        public int NestedAmount => _nested.Amount;
        public string NestedLabel => _nested.Label;
        public IReadOnlyList<int> Values => _values;
        public string LocalNote => _localNote;

        public void SetNested(int amount, string label)
        {
            _nested.Set(amount, label);
        }

        public void SetValues(params int[] values)
        {
            _values = new List<int>(values);
        }

        public void SetLocalNote(string value)
        {
            _localNote = value;
        }
    }

    [Serializable]
    public sealed class ScriptableVariantTestNestedData
    {
        [SerializeField]
        private int _amount;

        [SerializeField]
        private string _label;

        public int Amount => _amount;
        public string Label => _label;

        public void Set(int amount, string label)
        {
            _amount = amount;
            _label = label;
        }
    }
}
