using System;
using System.Collections.Generic;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Result of one search: the capped rows plus the true match count, so the
    /// status line can say "N matches" while only a few rows are rendered.
    /// </summary>
    public sealed class SearchQueryResult
    {
        public static readonly SearchQueryResult Empty =
            new SearchQueryResult(new SearchEntry[0], 0, false);

        public SearchQueryResult(IReadOnlyList<SearchEntry> items, int totalMatches, bool limitReached)
        {
            Items = items ?? new SearchEntry[0];
            TotalMatches = totalMatches;
            LimitReached = limitReached;
        }

        public IReadOnlyList<SearchEntry> Items { get; private set; }
        public int TotalMatches { get; private set; }
        public bool LimitReached { get; private set; }
    }
}
