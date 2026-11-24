using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace HvacAiCadAddin.Norm
{
    // HVACNORM file exchange: the DXF snapshot of the selection together with the map from the
    // snapshot's handles back to the drawing's objects (WblockCloneObjects gives the clones new handles),
    // the request file and the work folder. Python runs as a separate process (RoomsExchange.RunPython).
    internal static class NormExchange
    {
        /// <summary>A new, empty folder under %TEMP%\HvacAiCadAddin\norm for one run.</summary>
        internal static string NewWorkFolder()
        {
            string folder = Path.Combine(
                Path.GetTempPath(), "HvacAiCadAddin", "norm",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Copies the selected entities into a new database, saves it as DXF and returns snapshot handle
        /// (hex, upper case) -> object id in the drawing, for the selected entities themselves (objects
        /// inside blocks are not mapped: they cannot be changed). The drawing is not changed.
        /// </summary>
        internal static Dictionary<string, ObjectId> WriteSnapshot(Database db, IList<ObjectId> ids, string dxfPath)
        {
            Dictionary<string, ObjectId> handles = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            HashSet<ObjectId> selected = new HashSet<ObjectId>(ids);
            using (Database side = new Database(true, true))
            {
                ObjectIdCollection collection = new ObjectIdCollection();
                foreach (ObjectId id in ids)
                    collection.Add(id);
                IdMapping mapping = new IdMapping();
                db.WblockCloneObjects(
                    collection, SymbolUtilityServices.GetBlockModelSpaceId(side), mapping,
                    DuplicateRecordCloning.Ignore, false);
                foreach (IdPair pair in mapping)
                {
                    if (pair.IsCloned && selected.Contains(pair.Key) && !pair.Value.IsNull)
                        handles[pair.Value.Handle.ToString()] = pair.Key;
                }

                side.DxfOut(dxfPath, 16, DwgVersion.Current);
            }

            return handles;
        }

        internal static void WriteRequest(string path, NormRequest request)
        {
            File.WriteAllText(path, NormExchangeCore.ToJson(request), new UTF8Encoding(false));
        }
    }
}
