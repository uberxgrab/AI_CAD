using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;

namespace HvacAiCadAddin.Rooms
{
    // HVACROOMS file exchange with the Python back end: the layers present in a selection (for the
    // dialog), the DXF snapshot of the selection, the request file, and the separate python.exe
    // process. Python never runs inside AutoCAD (CLAUDE.md).
    internal static class RoomsExchange
    {
        private const int MaxBlockDepth = 8;

        /// <summary>
        /// Layer names used by the selected entities and by everything inside selected block
        /// references (nested blocks and bound xrefs included), with entity counts. An entity on
        /// layer 0 inside a block takes the layer of the reference that holds it.
        /// </summary>
        internal static SortedDictionary<string, int> CollectLayers(Database db, IEnumerable<ObjectId> ids)
        {
            SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<ObjectId, Dictionary<string, int>> cache = new Dictionary<ObjectId, Dictionary<string, int>>();
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null)
                        continue;
                    Add(counts, entity.Layer, 1);
                    BlockReference reference = entity as BlockReference;
                    if (reference == null || entity is Table)
                        continue;
                    foreach (KeyValuePair<string, int> inner in BlockLayers(transaction, reference.BlockTableRecord, cache, 0))
                        Add(counts, inner.Key == "0" ? entity.Layer : inner.Key, inner.Value);
                }

                transaction.Commit();
            }

            return counts;
        }

        // Layer counts of one block definition; "0" is left for the caller to resolve.
        private static Dictionary<string, int> BlockLayers(
            Transaction transaction, ObjectId blockId, Dictionary<ObjectId, Dictionary<string, int>> cache, int depth)
        {
            Dictionary<string, int> counts;
            if (cache.TryGetValue(blockId, out counts))
                return counts;

            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            cache[blockId] = counts; // guards against self-referencing blocks
            BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead) as BlockTableRecord;
            if (block == null || depth > MaxBlockDepth)
                return counts;

            foreach (ObjectId id in block)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity is AttributeDefinition)
                    continue;
                Add(counts, entity.Layer, 1);
                BlockReference nested = entity as BlockReference;
                if (nested == null || entity is Table)
                    continue;
                foreach (KeyValuePair<string, int> inner in BlockLayers(transaction, nested.BlockTableRecord, cache, depth + 1))
                    Add(counts, inner.Key == "0" ? entity.Layer : inner.Key, inner.Value);
            }

            return counts;
        }

        private static void Add(IDictionary<string, int> counts, string layer, int count)
        {
            int known;
            counts.TryGetValue(layer, out known);
            counts[layer] = known + count;
        }

        /// <summary>A new, empty folder under %TEMP%\HvacAiCadAddin\rooms for one run.</summary>
        internal static string NewWorkFolder()
        {
            string folder = Path.Combine(
                Path.GetTempPath(), "HvacAiCadAddin", "rooms",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Copies the selected entities (with the blocks, layers and styles they use) into a new
        /// database and saves it as DXF. The drawing itself is not changed.
        /// </summary>
        internal static void WriteSnapshot(Database db, IList<ObjectId> ids, string dxfPath)
        {
            using (Database side = new Database(true, true))
            {
                ObjectIdCollection collection = new ObjectIdCollection();
                foreach (ObjectId id in ids)
                    collection.Add(id);
                IdMapping mapping = new IdMapping();
                db.WblockCloneObjects(
                    collection, SymbolUtilityServices.GetBlockModelSpaceId(side), mapping,
                    DuplicateRecordCloning.Ignore, false);
                side.DxfOut(dxfPath, 16, DwgVersion.Current);
            }
        }

        internal static void WriteRequest(string path, RoomsRequest request)
        {
            File.WriteAllText(path, RoomsExchangeCore.ToJson(request), new UTF8Encoding(false));
        }

        /// <summary>
        /// Runs python.exe on the script with the request and waits up to <paramref name="timeoutMs"/>.
        /// Returns the exit code, or -1 when it timed out (the process is then killed).
        /// </summary>
        internal static int RunPython(string python, string script, string requestPath, int timeoutMs, out string output)
        {
            ProcessStartInfo start = new ProcessStartInfo(python, RoomsExchangeCore.Arguments(script, requestPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script)
            };

            using (Process process = Process.Start(start))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    output = "Python did not finish within " + (timeoutMs / 1000) + " s.";
                    return -1;
                }

                process.WaitForExit(); // flushes the redirected streams
                output = (stdout.Result + "\n" + stderr.Result).Trim();
                return process.ExitCode;
            }
        }
    }
}
