using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(HvacAiCadAddin.Command))]
[assembly: CommandClass(typeof(HvacAiCadAddin.Message))]

namespace HvacAiCadAddin
{
    public sealed class Command
    {
        [CommandMethod("HVACAI")]
        public void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Database database = doc.Database;
            Editor editor = doc.Editor;

            // Main entry point for commands that use the active drawing.
            editor.WriteMessage("\nHVAC AI is ready for drawing: {0}", doc.Name);
        }
    }
}
