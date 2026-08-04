using System.Threading;
using System.Threading.Tasks;

namespace ZX0ai.Core.Services
{
    /// <summary>
    /// Service to verify DashScope credentials. Placeholder implementation is used
    /// until a real verification endpoint is supplied.
    /// </summary>
    public interface IDashScopeService
    {
        /// <summary>
        /// Attempts to verify the configured DashScope API key and returns a result tuple
        /// (verified, human-readable message).
        /// </summary>
        Task<(bool Verified, string Message)> VerifyAsync(CancellationToken cancellationToken = default);
    }
}
