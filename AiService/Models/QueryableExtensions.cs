using Microsoft.EntityFrameworkCore;

namespace AiService.Models
{
    public static class QueryableExtensions
    {
        public static async Task<ILookup<TKey, TElement>> ToLookupAsync<TSource, TKey, TElement>(
            this IQueryable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector)
        {
            var list = await source
                .Select(x => new { Key = keySelector(x), Element = elementSelector(x) })
                .ToListAsync();

            return list.ToLookup(x => x.Key, x => x.Element);
        }

        public static Task<ILookup<TKey, TSource>> ToLookupAsync<TSource, TKey>(
            this IQueryable<TSource> source,
            Func<TSource, TKey> keySelector)
            => ToLookupAsync(source, keySelector, x => x);
    }
}
