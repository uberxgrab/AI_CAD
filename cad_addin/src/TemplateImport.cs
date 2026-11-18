using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace HvacAiCadAddin.Commands
{
    // Brings block definitions and layers from the user's template drawing
    // (CAD/MEP_DWT_3.dwg) into the current drawing. The managed API cannot
    // author dynamic-block parameters or actions, so duct and refnet blocks are
    // always copied from the template rather than created in code.
    //
    // In a live database the template's dynamic definitions have their real
    // names (REC_600x300, Round D350, Branch Connection (1)); only DXF readers
    // see the anonymous *U names. Look blocks up by name here.
    internal sealed class TemplateSource : IDisposable
    {
        private readonly Database _template;

        private TemplateSource(Database template, string path)
        {
            _template = template;
            Path = path;
        }

        public string Path { get; private set; }

        // Opens the template read-only in a side database. Reading a DWG runs
        // no code. Returns null if the file cannot be read.
        public static TemplateSource TryOpen(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "Template drawing not found: " + path;
                return null;
            }

            Database database = new Database(false, true);
            try
            {
                database.ReadDwgFile(path, FileShare.Read, true, string.Empty);
                database.CloseInput(true);
                return new TemplateSource(database, path);
            }
            catch (System.Exception ex)
            {
                database.Dispose();
                error = "Could not read template " + path + ": " + ex.Message;
                return null;
            }
        }

        public bool HasBlock(string blockName)
        {
            using (Transaction transaction =
                _template.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(
                    _template.BlockTableId,
                    OpenMode.ForRead);
                bool found = table.Has(blockName);
                transaction.Commit();
                return found;
            }
        }

        // Copies the named block definitions into target. Definitions that
        // already exist in target are kept as they are
        // (DuplicateRecordCloning.Ignore). Returns name -> definition id in
        // target for every name that exists in target afterwards; names found
        // in neither drawing are listed in missing.
        public Dictionary<string, ObjectId> ImportBlocks(
            Database target,
            IEnumerable<string> blockNames,
            out List<string> missing)
        {
            missing = new List<string>();
            List<string> wanted = new List<string>(blockNames);
            ObjectIdCollection sourceIds = new ObjectIdCollection();

            using (Transaction transaction =
                _template.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(
                    _template.BlockTableId,
                    OpenMode.ForRead);
                foreach (string name in wanted)
                {
                    if (table.Has(name))
                        sourceIds.Add(table[name]);
                }

                transaction.Commit();
            }

            if (sourceIds.Count > 0)
            {
                IdMapping mapping = new IdMapping();
                _template.WblockCloneObjects(
                    sourceIds,
                    target.BlockTableId,
                    mapping,
                    DuplicateRecordCloning.Ignore,
                    false);
            }

            return LookUp(target, target.BlockTableId, wanted, missing);
        }

        // Copies the named layers into target, together with the linetypes
        // and text styles they reference (custom linetypes such as
        // "Broken Line" or CWR, which embeds text). Existing layers are kept.
        public Dictionary<string, ObjectId> ImportLayers(
            Database target,
            IEnumerable<string> layerNames,
            out List<string> missing)
        {
            missing = new List<string>();
            List<string> wanted = new List<string>(layerNames);
            ObjectIdCollection sourceIds = new ObjectIdCollection();

            using (Transaction transaction =
                _template.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(
                    _template.LayerTableId,
                    OpenMode.ForRead);
                foreach (string name in wanted)
                {
                    if (table.Has(name))
                        sourceIds.Add(table[name]);
                }

                transaction.Commit();
            }

            if (sourceIds.Count > 0)
            {
                IdMapping mapping = new IdMapping();
                _template.WblockCloneObjects(
                    sourceIds,
                    target.LayerTableId,
                    mapping,
                    DuplicateRecordCloning.Ignore,
                    false);
            }

            return LookUp(target, target.LayerTableId, wanted, missing);
        }

        // Layer names that exist in the template, for building pick lists.
        public List<string> GetLayerNames()
        {
            List<string> names = new List<string>();
            using (Transaction transaction =
                _template.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(
                    _template.LayerTableId,
                    OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
                        id,
                        OpenMode.ForRead);
                    names.Add(layer.Name);
                }

                transaction.Commit();
            }

            return names;
        }

        private static Dictionary<string, ObjectId> LookUp(
            Database target,
            ObjectId symbolTableId,
            List<string> names,
            List<string> missing)
        {
            Dictionary<string, ObjectId> result =
                new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction =
                target.TransactionManager.StartTransaction())
            {
                SymbolTable table = (SymbolTable)transaction.GetObject(
                    symbolTableId,
                    OpenMode.ForRead);
                foreach (string name in names)
                {
                    if (table.Has(name))
                        result[name] = table[name];
                    else
                        missing.Add(name);
                }

                transaction.Commit();
            }

            return result;
        }

        public void Dispose()
        {
            _template.Dispose();
        }
    }

    // Remembers where the template drawing is. The first time, it looks for
    // CAD\MEP_DWT_3.dwg above the add-in folder (the repository layout), and
    // otherwise asks the user to pick the file.
    internal static class TemplateLocator
    {
        internal const string DefaultFileName = "MEP_DWT_3.dwg";

        private static string SettingsPath
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "template.settings");
            }
        }

        // Returns null if the user cancels.
        internal static string GetTemplatePath(Editor editor, bool forcePrompt)
        {
            if (!forcePrompt)
            {
                string remembered = ReadRemembered();
                if (!string.IsNullOrEmpty(remembered) && File.Exists(remembered))
                    return remembered;

                string found = FindNearAssembly();
                if (found != null)
                {
                    Remember(found);
                    return found;
                }
            }

            if (editor == null)
                return null;

            PromptOpenFileOptions options = new PromptOpenFileOptions(
                "\nSelect the HVAC template drawing (" + DefaultFileName + ")");
            options.Filter = "Drawing (*.dwg)|*.dwg";
            PromptFileNameResult result = editor.GetFileNameForOpen(options);
            if (result.Status != PromptStatus.OK)
                return null;

            Remember(result.StringResult);
            return result.StringResult;
        }

        private static string FindNearAssembly()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(
                    typeof(TemplateLocator).Assembly.Location);
                for (int level = 0; level < 7 && !string.IsNullOrEmpty(directory); level++)
                {
                    string candidate = System.IO.Path.Combine(
                        directory, "CAD", DefaultFileName);
                    if (File.Exists(candidate))
                        return candidate;
                    directory = System.IO.Path.GetDirectoryName(directory);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }

            return null;
        }

        private static string ReadRemembered()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return File.ReadAllText(SettingsPath).Trim();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return null;
        }

        private static void Remember(string path)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, path);
            }
            catch (IOException)
            {
                // Remembering the path is a convenience only.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
