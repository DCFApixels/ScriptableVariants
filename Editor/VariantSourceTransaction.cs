using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    // Filesystems have no multi-file atomic replace. A write-ahead journal makes a batch
    // recoverable, while preflight + rollback handle ordinary exceptions without publishing it.
    internal static class VariantSourceTransaction
    {
        internal sealed class Change
        {
            public string Path;
            public string Before;
            public string After;
        }

        private sealed class Journal
        {
            public List<Change> Changes = new List<Change>();
        }

        private static string JournalFolder => System.IO.Path.GetFullPath("Library/ScriptableVariants/Transactions");
        private static bool _recovering;

        internal static List<Change> Commit(IReadOnlyList<string> paths,
            IReadOnlyList<VariantSourceDocument> documents)
        {
            if (paths.Count != documents.Count) throw new ArgumentException("Mismatched transaction inputs.");
            Directory.CreateDirectory(JournalFolder);
            using var transactionLock = new FileStream(System.IO.Path.Combine(JournalFolder, ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Never build a newer edit on top of a possibly half-committed batch. A conflicted
            // recovery journal blocks writes until its sources have been reconciled explicitly.
            foreach (var pending in Directory.GetFiles(JournalFolder, "*.json")) RecoverJournal(pending);
            var changes = new List<Change>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var fullPath = GetSourcePath(path);
                if (!seen.Add(fullPath)) throw new ArgumentException($"Duplicate transaction target '{path}'.");
                var before = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
                var after = VariantSourceDatabase.SerializeDocument(documents[i]);
                if (System.Text.Encoding.UTF8.GetByteCount(after) > 32 * 1024 * 1024)
                    throw new IOException("Variant sources larger than 32 MiB are not supported.");
                VariantSourceDatabase.DeserializeDocument(after).Normalize(); // reject unreadable output before touching disk
                VariantSourceDatabase.AssertRevision(path, documents[i].SourceJson);
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;
                if (before != null && !AssetDatabase.MakeEditable(path))
                    throw new IOException($"Variant source is not editable: {path}");
                changes.Add(new Change {Path = path, Before = before, After = after});
            }
            if (changes.Count == 0) return changes;

            var journalPath = System.IO.Path.Combine(JournalFolder, Guid.NewGuid().ToString("N") + ".json");
            WriteJournal(journalPath, new Journal {Changes = changes});
            try
            {
                // Check again after checkout/journal preparation, immediately before replacing files.
                foreach (var change in changes) VariantSourceDatabase.AssertRevision(change.Path, change.Before);
                foreach (var change in changes)
                {
                    VariantSourceDatabase.AssertRevision(change.Path, change.Before);
                    VariantSourceDatabase.WriteSourceAtomically(GetSourcePath(change.Path), change.After);
                }
            }
            catch
            {
                // Leave the journal if rollback itself fails. Never overwrite a third-party revision.
                Rollback(changes);
                File.Delete(journalPath);
                throw;
            }
            // If deletion fails, recovery recognizes an entirely committed batch and only removes its journal.
            try { File.Delete(journalPath); }
            catch (IOException exception) { Debug.LogWarning($"Variant transaction journal retained: {exception.Message}"); }
            catch (UnauthorizedAccessException exception) { Debug.LogWarning($"Variant transaction journal retained: {exception.Message}"); }
            return changes;
        }

        internal static string GetSourcePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || System.IO.Path.IsPathRooted(assetPath) ||
                !(assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                  assetPath.StartsWith("Packages/", StringComparison.Ordinal)) ||
                !VariantSourceDatabase.IsVariantSourcePath(assetPath))
                throw new ArgumentException($"Not a writable project variant path: '{assetPath}'.");
            var path = System.IO.Path.GetFullPath(FileUtil.GetPhysicalPath(assetPath));
            var project = System.IO.Path.GetFullPath(".") + System.IO.Path.DirectorySeparatorChar;
            if (!path.StartsWith(project, StringComparison.OrdinalIgnoreCase) ||
                assetPath.Replace('\\', '/').Contains("/../"))
                throw new IOException($"Variant source is outside the project: '{assetPath}'.");
            return path;
        }

        internal static void Recover()
        {
            if (_recovering || AssetDatabase.IsAssetImportWorkerProcess() || !Directory.Exists(JournalFolder)) return;
            _recovering = true;
            try
            {
                using var transactionLock = new FileStream(System.IO.Path.Combine(JournalFolder, ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                foreach (var file in Directory.GetFiles(JournalFolder, "*.json"))
                {
                    try
                    {
                        RecoverJournal(file);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError($"Variant transaction recovery requires attention. Backup kept at '{file}': {exception.Message}");
                    }
                }
            }
            catch (IOException exception)
            {
                Debug.LogWarning($"Variant transaction recovery is deferred: {exception.Message}");
            }
            finally { _recovering = false; }
        }

        private static void RecoverJournal(string file)
        {
            Journal journal;
            using (var text = File.OpenText(file))
            using (var reader = new JsonTextReader(text) {MaxDepth = 16, DateParseHandling = DateParseHandling.None})
                journal = JsonSerializer.Create(new JsonSerializerSettings
                {TypeNameHandling = TypeNameHandling.None, CheckAdditionalContent = true,
                    MissingMemberHandling = MissingMemberHandling.Error}).Deserialize<Journal>(reader);
            if (journal?.Changes == null || journal.Changes.Count == 0) throw new IOException($"Empty transaction journal: {file}");
            var committed = true;
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in journal.Changes)
            {
                if (change == null || change.After == null) throw new IOException($"Incomplete transaction journal: {file}");
                var path = GetSourcePath(change.Path);
                if (!targets.Add(path)) throw new IOException($"Duplicate recovery target: {file}");
                var actual = File.Exists(path) ? File.ReadAllText(path) : null;
                committed &= string.Equals(actual, change.After, StringComparison.Ordinal);
            }
            if (!committed) Rollback(journal.Changes);
            foreach (var change in journal.Changes)
            {
                VariantSourceDatabase.Invalidate(change.Path);
                VariantSourceDatabase.QueueImport(change.Path);
            }
            File.Delete(file);
        }

        private static void Rollback(IReadOnlyList<Change> changes)
        {
            // Validate every target before changing any of them, including recovery after an editor crash.
            foreach (var change in changes)
            {
                var path = GetSourcePath(change.Path);
                var actual = File.Exists(path) ? File.ReadAllText(path) : null;
                if (!string.Equals(actual, change.Before, StringComparison.Ordinal) &&
                    !string.Equals(actual, change.After, StringComparison.Ordinal))
                    throw new IOException($"Concurrent modification prevents transaction rollback: {change.Path}");
            }
            for (var i = changes.Count - 1; i >= 0; i--)
            {
                var change = changes[i];
                var path = GetSourcePath(change.Path);
                var actual = File.Exists(path) ? File.ReadAllText(path) : null;
                if (string.Equals(actual, change.Before, StringComparison.Ordinal)) continue;
                if (change.Before == null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else VariantSourceDatabase.WriteSourceAtomically(path, change.Before);
            }
        }

        private static void WriteJournal(string path, Journal journal)
        {
            var temporary = path + ".pending";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var text = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, true))
                using (var writer = new JsonTextWriter(text))
                {
                    JsonSerializer.Create(new JsonSerializerSettings {TypeNameHandling = TypeNameHandling.None}).Serialize(writer, journal);
                    writer.Flush();
                    text.Flush();
                    stream.Flush(true);
                }
                File.Move(temporary, path); // recovery sees only a fully flushed journal
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
