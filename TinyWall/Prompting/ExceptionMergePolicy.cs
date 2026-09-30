using System;

namespace pylorak.TinyWall.Prompting
{
    internal static class ExceptionMergePolicy
    {
        // Subject equality is one-way across subclasses: ExecutableSubject.Equals
        // accepts a ServiceSubject on path alone, while ServiceSubject.Equals rejects
        // an ExecutableSubject. Merging on the one-way match would move an
        // executable-wide rule (a block, or an allow for every hosted service) onto
        // one service. Only subjects equal in both directions may merge; for two
        // subjects of the same type this is the same as the one-way test.
        internal static bool SameSubject<T>(T? first, T? second)
            where T : class, IEquatable<T> =>
            first != null && second != null && first.Equals(second) && second.Equals(first);
    }
}
