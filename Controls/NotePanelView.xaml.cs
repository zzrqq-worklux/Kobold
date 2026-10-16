using System.Windows.Controls;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// Skeleton of the note widget's content (Task T10): the add row and the
    /// list host. The renderer that fills them - the cards, the reading and
    /// editing states, the menus and the undo stack - arrives with
    /// T11/T13/T14/T15.
    /// </summary>
    public partial class NotePanelView : UserControl
    {
        private NoteData _data;
        private NoteWidget _host;

        public NotePanelView()
        {
            InitializeComponent();
        }

        /// <summary>Binds the view to its widget's data and host window.</summary>
        public void Attach(NoteData data, NoteWidget host)
        {
            _data = data ?? new NoteData();
            _host = host;
        }

        /// <summary>
        /// Re-renders the list. The skeleton paints no cards yet (the renderer
        /// lands with T11), so the list host stays empty and the add row is
        /// the panel's only content; the guard keeps the view inert until it
        /// was attached.
        /// </summary>
        public void Refresh()
        {
            if (_data == null || _host == null) return;
        }
    }
}
