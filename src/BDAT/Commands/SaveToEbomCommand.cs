using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT.Commands
{
    /// <summary>
    /// Save to EBOM: a part made on this PC goes into 3DX as an EBOM part in one go, instead of uploading it and then
    /// renaming it and typing its description by hand. Pick its EBOM row and it's saved as e.g. BR-10101-AA with that
    /// row's Description and Part Number, in its assembly's folder, and checked in. Shares Open from EBOM's picker,
    /// folder logic and fresh "already in 3DX?" check (NewFromEbomCommand.SaveOpenPart). With an assembly open, each of
    /// its files that's only on this PC (or each selected component) gets its own row (NewFromEbomCommand.SaveOpenAssembly).
    /// </summary>
    public sealed class SaveToEbomCommand : IBdatCommand
    {
        public string Title { get { return "Save to EBOM"; } }

        public string Hint { get { return "Save the open part, or an assembly and its parts, to 3DX as EBOM parts: named, described and put in their folders"; } }

        public bool IsEnabled(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            return doc != null && (doc.GetType() == (int)swDocumentTypes_e.swDocPART || doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY);
        }

        public void Run(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                new NewFromEbomCommand().SaveOpenAssembly(swApp, Title);
            else
                new NewFromEbomCommand().SaveOpenPart(swApp, Title);
        }
    }
}
