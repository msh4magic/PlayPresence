using System.Threading;
using System.Threading.Tasks;
using PlayPresence.Models;

namespace PlayPresence.Services.Metadata
{
    /// <summary>
    /// Resolves a launched game into presentation-ready metadata (artwork, developer, year, …),
    /// using IGDB with intelligent ROM-name matching, caching and graceful fallbacks.
    /// </summary>
    public interface IMetadataResolver
    {
        Task<ResolvedMetadata> ResolveAsync(GameContext context, CancellationToken cancellationToken);

        /// <summary>Removes any cached metadata for the given game so the next resolve refetches it.</summary>
        void Invalidate(GameContext context);
    }
}
