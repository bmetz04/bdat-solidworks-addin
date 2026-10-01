using SolidWorks.Interop.sldworks;

namespace BDAT.Commands
{
    /// <summary>One button on the BDAT toolbar.</summary>
    public interface IBdatCommand
    {
        /// <summary>Button and menu text.</summary>
        string Title { get; }

        /// <summary>Tooltip / status bar text.</summary>
        string Hint { get; }

        /// <summary>Whether the button is clickable right now (e.g. only when a part is open).</summary>
        bool IsEnabled(ISldWorks swApp);

        void Run(ISldWorks swApp);
    }
}
