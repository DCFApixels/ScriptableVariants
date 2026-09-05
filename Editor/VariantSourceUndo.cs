using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    // A source snapshot has an Undo lifetime independent of the Inspector and its temporary SO.
    // GUIDs keep Undo attached to the source if it is renamed or moved.
    internal sealed class VariantSourceUndo : ScriptableObject
    {
        [SerializeField] private string _guid;
        [SerializeField] private string _json;
        [NonSerialized] private string _appliedJson;

        private static readonly Dictionary<string, VariantSourceUndo> States =
            new Dictionary<string, VariantSourceUndo>(StringComparer.Ordinal);

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            States.Clear();
            foreach (var state in Resources.FindObjectsOfTypeAll<VariantSourceUndo>())
            {
                state._appliedJson = state._json;
                States[state._guid] = state;
            }

            Undo.undoRedoPerformed -= RestoreSources;
            Undo.undoRedoPerformed += RestoreSources;
        }

        internal static void Record(string assetPath, string previousJson, string nextJson)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid) || previousJson == null)
            {
                return; // Creating an asset is not a field edit.
            }

            if (!States.TryGetValue(guid, out var state) || state == null)
            {
                state = CreateInstance<VariantSourceUndo>();
                state.hideFlags = HideFlags.HideAndDontSave;
                state._guid = guid;
                state._json = VariantSourceDatabase.SerializeDocument(
                    VariantSourceDatabase.DeserializeDocument(previousJson));
                States[guid] = state;
            }
            else if (!string.Equals(state._appliedJson, previousJson, StringComparison.Ordinal))
            {
                // External source edits start a new history instead of Undo restoring stale JSON.
                Undo.ClearUndo(state);
                state._json = VariantSourceDatabase.SerializeDocument(
                    VariantSourceDatabase.DeserializeDocument(previousJson));
            }

            Undo.RegisterCompleteObjectUndo(state, "Edit Scriptable Variant");
            state._json = nextJson;
            state._appliedJson = nextJson;
        }

        private static void RestoreSources()
        {
            foreach (var state in States.Values)
            {
                if (state == null || string.Equals(state._json, state._appliedJson, StringComparison.Ordinal))
                {
                    continue;
                }

                var path = AssetDatabase.GUIDToAssetPath(state._guid);
                if (string.IsNullOrEmpty(path) || !File.Exists(FileUtil.GetPhysicalPath(path)))
                {
                    continue;
                }

                try
                {
                    if (!string.Equals(File.ReadAllText(FileUtil.GetPhysicalPath(path)),
                            state._appliedJson, StringComparison.Ordinal))
                    {
                        // Never overwrite a newer external edit when undoing an old Inspector edit.
                        Undo.ClearUndo(state);
                        state._json = state._appliedJson = File.ReadAllText(FileUtil.GetPhysicalPath(path));
                        continue;
                    }

                    var document = VariantSourceDatabase.DeserializeDocument(state._json);
                    VariantSourceDatabase.Save(path, document, recordUndo: false);
                    state._appliedJson = VariantSourceDatabase.SerializeDocument(document);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"Could not restore variant source '{path}': {exception.Message}");
                }
            }

            // Finish restoring every source first (Apply to Parent changes two files).
            VariantEditingSession.ReloadOpenSessions();
        }
    }
}
