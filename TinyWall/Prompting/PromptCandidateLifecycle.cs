namespace pylorak.TinyWall.Prompting
{
    internal static class PromptCandidateLifecycle
    {
        internal static void Reset(PromptQueue queue, CorrelatedDropBatch correlated,
            DropCandidateBuffer candidates, bool stop = false, bool revokeTokens = true)
        {
            lock (queue.SyncRoot)
            {
                correlated.Reset(stop);
                candidates.Clear();
                // A failed replacement leaves the previous policy authoritative. Invalidate
                // in-flight attribution without destroying its still-actionable prompts.
                // Stop always revokes, and no path restores a previously revoked token.
                if (stop || revokeTokens) queue.Clear();
            }
        }
    }
}
