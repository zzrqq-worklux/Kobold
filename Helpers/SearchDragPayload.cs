using System.Collections.Specialized;
using System.Windows;

namespace Kobold.Helpers
{
    /// <summary>
    /// The drag payload of one search result: the plain FileDrop list Explorer
    /// and the folder widget's external-drop path already understand. A file
    /// publishes its path, a folder its directory path; the drop target owns
    /// the copy/move decision.
    /// </summary>
    public static class SearchDragPayload
    {
        public static DataObject Create(string path)
        {
            var files = new StringCollection();
            files.Add(path);

            var data = new DataObject();
            data.SetFileDropList(files);
            return data;
        }
    }
}
