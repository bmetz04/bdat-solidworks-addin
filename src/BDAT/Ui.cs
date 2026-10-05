using System;
using System.Windows.Forms;
using BDAT.Testing;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT
{
    /// <summary>
    /// Every message box BDAT shows goes through here, so that in test mode they are recorded and answered
    /// automatically instead of waiting for a click. Outside test mode they're BDAT's own message box (ModernMessage),
    /// in the same look as its pop-ups, centred on SolidWorks.
    /// </summary>
    internal static class Ui
    {
        /// <summary>A message with an OK button, over SolidWorks.</summary>
        public static void Tell(ISldWorks swApp, string message, swMessageBoxIcon_e icon)
        {
            if (TestMode.Enabled)
            {
                TestMode.Record("BDAT", message);
                return;
            }
            ModernMessage.Show(SolidWorksWindow(swApp), message, "BDAT", MessageBoxButtons.OK, IconFor(icon));
        }

        /// <summary>A Yes/No question over SolidWorks. True for Yes.</summary>
        public static bool AskYesNo(ISldWorks swApp, string message)
        {
            if (TestMode.Enabled)
            {
                TestMode.Record("BDAT", message);
                return TestMode.NextAnswer();
            }
            return ModernMessage.Show(SolidWorksWindow(swApp), message, "BDAT", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>A message box over owner. In test mode a "yes" answer is Yes/OK and a "no" answer is No/Cancel.</summary>
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
            return ModernMessage.Show(owner, message, caption, buttons, icon);
        }

        /// <summary>
        /// A question with buttons of your own; returns the index clicked (the last button also means Esc / close).
        /// In test mode: the first choice for a "yes" answer, the last for a "no".
        /// </summary>
        public static int Choose(IWin32Window owner, string message, string caption, MessageBoxIcon icon, params string[] choices)
        {
            if (TestMode.Enabled)
            {
                TestMode.Record(caption, message);
                return TestMode.NextAnswer() ? 0 : choices.Length - 1;
            }
            return ModernMessage.Choose(owner, message, caption, icon, choices);
        }

        private static MessageBoxIcon IconFor(swMessageBoxIcon_e icon)
        {
            switch (icon)
            {
                case swMessageBoxIcon_e.swMbStop: return MessageBoxIcon.Error;
                case swMessageBoxIcon_e.swMbWarning: return MessageBoxIcon.Warning;
                case swMessageBoxIcon_e.swMbQuestion: return MessageBoxIcon.Question;
                default: return MessageBoxIcon.Information;
            }
        }

        /// <summary>SolidWorks' main window, to own BDAT's pop-ups. Null if it can't be found.</summary>
        internal static IWin32Window SolidWorksWindow(ISldWorks swApp)
        {
            try
            {
                IFrame frame = swApp == null ? null : swApp.Frame() as IFrame;
                return frame == null ? null : new WindowHandle(new IntPtr(frame.GetHWndx64()));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class WindowHandle : IWin32Window
        {
            private readonly IntPtr _handle;
            public WindowHandle(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }
    }
}
