namespace Kobold.Core
{
    /// <summary>Search widget data: shared fields only; query text is never persisted.</summary>
    public class SearchData : WidgetData
    {
        public SearchData() { Kind = "search"; }
    }
}
