using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                state._json = previousJson;
                States[guid] = state;
            }
            else if (!string.Equals(state._appliedJson, previousJson, StringComparison.Ordinal))
            {
                // External source edits start a new history instead of Undo restoring stale JSON.
                Undo.ClearUndo(state);
                state._json = previousJson;
            }

            Undo.RegisterCompleteObjectUndo(state, "Edit Scriptable Variant");
            state._json = nextJson;
            state._appliedJson = nextJson;
        }

        private static void RestoreSources()
        {
            var changed = States.Values.Where(state => state != null &&
                !string.Equals(state._json, state._appliedJson, StringComparison.Ordinal)).ToArray();
            var paths = new List<string>();
            var documents = new List<VariantSourceDocument>();
            try
            {
                foreach (var state in changed)
                {
                    var path = AssetDatabase.GUIDToAssetPath(state._guid);
                    if (string.IsNullOrEmpty(path)) throw new IOException("An Undo source was deleted.");
                    VariantSourceDatabase.AssertRevision(path, state._appliedJson);
                    var document = VariantSourceDatabase.DeserializeDocument(state._json);
                    document.SourceJson = state._appliedJson;
                    paths.Add(path);
                    documents.Add(document);
                }
                VariantSourceDatabase.SaveBatch(paths, documents, recordUndo: false);
                for (var i = 0; i < changed.Length; i++)
                    changed[i]._appliedJson = changed[i]._json = VariantSourceDatabase.SerializeDocument(documents[i]);
            }
            catch (Exception exception)
            {
                // Reject the entire Undo batch, not just the conflicted half of Apply to Parent.
                foreach (var state in changed)
                {
                    Undo.ClearUndo(state);
                    state._json = state._appliedJson;
                }
                Debug.LogError($"Could not restore variant source transaction: {exception.Message}");
            }

            // Finish restoring every source first (Apply to Parent changes two files).
            VariantEditingSession.ReloadOpenSessions();
        }

        internal static void PruneDeletedSources()
        {
            foreach (var pair in States.ToArray())
            {
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(pair.Key))) continue;
                States.Remove(pair.Key);
                if (pair.Value == null) continue;
                Undo.ClearUndo(pair.Value);
                DestroyImmediate(pair.Value);
            }
        }
    }
}
