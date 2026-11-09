using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT.Commands
{
    /// <summary>
    /// Save to EBOM: a part made on this PC goes into 3DX as an EBOM part in one go, instead of uploading it and then
    /// renaming it and typing its description by hand. Pick its EBOM row and it's saved as e.g. BR-10101-AA with that
    /// row's Description and Part Number, in its assembly's folder, and checked in. Shares Open from EBOM's picker,
    /// folder logic and fresh "already in 3DX?" check (NewFromEbomCommand.SaveOpenPart).
    /// </summary>
    public sealed class SaveToEbomCommand : IBdatCommand
    {
        public string Title { get { return "Save to EBOM"; } }

        public string Hint { get { return "Save the open part to 3DX as an EBOM part: named, described and put in its folder"; } }

        public bool IsEnabled(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART;
        }

        public void Run(ISldWorks swApp)
        {
            new NewFromEbomCommand().SaveOpenPart(swApp, Title);
        }
    }
}
