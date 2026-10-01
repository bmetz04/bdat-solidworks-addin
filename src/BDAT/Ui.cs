using System.Windows.Forms;
using BDAT.Testing;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT
{
    /// <summary>
    /// Every message box BDAT shows goes through here, so that in test mode they are recorded and answered
    /// automatically instead of waiting for a click.
    /// </summary>
    internal static class Ui
    {
        /// <summary>A SolidWorks message box with an OK button.</summary>
        public static void Tell(ISldWorks swApp, string message, swMessageBoxIcon_e icon)
        {
            if (TestMode.Enabled)
            {
                TestMode.Record("BDAT", message);
                return;
            }
            swApp.SendMsgToUser2(message, (int)icon, (int)swMessageBoxBtn_e.swMbOk);
        }

        /// <summary>A SolidWorks Yes/No question. True for Yes.</summary>
        public static bool AskYesNo(ISldWorks swApp, string message)
        {
            if (TestMode.Enabled)
            {
                TestMode.Record("BDAT", message);
                return TestMode.NextAnswer();
            }
            int answer = swApp.SendMsgToUser2(message,
                (int)swMessageBoxIcon_e.swMbQuestion, (int)swMessageBoxBtn_e.swMbYesNo);
            return answer == (int)swMessageBoxResult_e.swMbHitYes;
        }

        /// <summary>A Windows message box. In test mode a "yes" answer is Yes/OK and a "no" answer is No/Cancel.</summary>
        public static DialogResult Show(IWin32Window owner, string message, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            if (TestMode.Enabled)
            {
                TestMode.Record(caption, message);
                if (buttons == MessageBoxButtons.OK) return DialogResult.OK;
                bool yes = TestMode.NextAnswer();
                if (buttons == MessageBoxButtons.YesNo || buttons == MessageBoxButtons.YesNoCancel)
                    return yes ? DialogResult.Yes : DialogResult.No;
                return yes ? DialogResult.OK : DialogResult.Cancel;
            }
            return owner == null
                ? MessageBox.Show(message, caption, buttons, icon)
                : MessageBox.Show(owner, message, caption, buttons, icon);
        }
    }
}
