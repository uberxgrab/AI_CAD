using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace HvacAiCadAddin
{
    public sealed class Message
    {
        [CommandMethod("FIRSTMESSAGE")]
        public void Hello()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor editor = doc?.Editor;
            editor?.WriteMessage("\nHello from HVAC AI!");
        }
    }
}
