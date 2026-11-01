using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    // Public keys that may sign a release manifest: the owner's primary key (Windows key
    // store, non-exportable) and the offline backup key. Either verifies. Each equals the
    // key line of tools/release/keys/<name>.pub; a core test compares them. The updater
    // test key is never embedded here.
    internal static class ReleaseKeys
    {
        internal const string Primary = "";
        internal const string Backup = "";

        internal static IReadOnlyList<string> Trusted { get; } = Build(Primary, Backup);

        private static string[] Build(params string[] keys) => Array.FindAll(keys, key => key.Length != 0);
    }
}
