using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ZX0ai.Core.Services
{
    /// <summary>
    /// Placeholder DashScope verifier — returns an informational "not configured" result.
    /// A real implementation should perform a network call to the DashScope verification
    /// endpoint and return success/failure along with details.
    /// </summary>
    public sealed class DashScopeService : IDashScopeService
    {
        private readonly ILogger<DashScopeService> _logger;

        public DashScopeService(ILogger<DashScopeService> logger)
        {
            _logger = logger;
        }

        public Task<(bool Verified, string Message)> VerifyAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("DashScope verification requested — placeholder implementation.");
            return Task.FromResult((false, "DashScope verification is not configured; placeholder service in use."));
        }
    }
}
