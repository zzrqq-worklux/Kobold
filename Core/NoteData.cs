using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Data for a note widget: one list of notes. Timestamps carry full local
    /// time - unlike todos, they are never normalized to whole days.
    /// </summary>
    public class NoteData : WidgetData
    {
        public List<NoteItem> Notes { get; set; }

        private double _fontScale = NoteFontScale.Medium;

        /// <summary>The body-size multiplier (0.9 / 1.0 / 1.2); invalid values normalize.</summary>
        public double FontScale
        {
            get { return _fontScale; }
            set { _fontScale = NoteFontScale.Normalize(value); }
        }

        public NoteData()
        {
            Id = Guid.NewGuid().ToString();
            Color = UiTokens.DefaultFolderColor;
            Notes = new List<NoteItem>();
            Kind = "note";
        }
    }

    /// <summary>One note: the markdown source plus its two timestamps.</summary>
    public class NoteItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }
}
