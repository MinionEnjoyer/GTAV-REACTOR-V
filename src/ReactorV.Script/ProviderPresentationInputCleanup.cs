using RageWebUI.Core;

namespace RageWebUI.Script
{
    /// <summary>
    /// Revoke only the terminal presentation's local authority. Pending (not
    /// yet bound) input is deliberately outside this operation: a late failure
    /// must not consume the user's next opening edge or a replacement's epoch.
    /// </summary>
    internal static class ProviderPresentationInputCleanup
    {
        internal static bool TryRevoke(
            string? presentationId,
            ref long boundEpoch,
            ref string? boundPresentationId,
            ref string? fallbackPresentationId,
            out long revokedEpoch)
        {
            revokedEpoch = 0;
            if (!ProviderPresentationCommitContract.IsValidPresentationId(presentationId))
                return false;

            var boundMatches = ProviderPresentationCommitContract.Matches(
                boundPresentationId, presentationId);
            var fallbackMatches = ProviderPresentationCommitContract.Matches(
                fallbackPresentationId, presentationId);
            if (boundMatches)
            {
                revokedEpoch = boundEpoch > 0 ? boundEpoch : 0;
                boundEpoch = 0;
                boundPresentationId = null;
            }
            if (fallbackMatches)
                fallbackPresentationId = null;
            return boundMatches || fallbackMatches;
        }
    }
}
