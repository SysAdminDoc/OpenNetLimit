using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.Security;

public interface IProcessVerifier
{
    Task<ProcessVerificationInfo> VerifyFileAsync(string processPath, CancellationToken ct = default);
    IReadOnlyList<ProcessVerificationInfo> GetCachedResults();
}
