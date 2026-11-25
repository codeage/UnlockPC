using Microsoft.UI.Xaml.Controls;

namespace WinUIKit
{
    /// <summary>Shell of the dialog window (title bar, body, command area); used only by <see cref="DialogWindow"/>.</summary>
    internal sealed partial class DialogWindowShell : UserControl
    {
        public DialogWindowShell()
        {
            InitializeComponent();
        }
    }
}
