using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace pylorak.TinyWall
{
    // "Always allow" an application by file name and signer, whatever folder it runs from.
    // WFP filters match an exact executable path only, so this rule is never a filter: when
    // a matching file is blocked, the service checks its Authenticode signature and, if the
    // signer matches, adds an ordinary outbound-only exception for that exact path.
    [DataContract(Namespace = "TinyWall")]
    public sealed class AlwaysAllowRule
    {
        // File name only, for example "claude.exe". Compared case-insensitively.
        [DataMember(EmitDefaultValue = false)]
        public string FileName { get; set; } = string.Empty;

        // Full subject of the signing certificate, compared exactly.
        [DataMember(EmitDefaultValue = false)]
        public string Signer { get; set; } = string.Empty;

        // Display name of the signer (the certificate's common name).
        [DataMember(EmitDefaultValue = false)]
        public string SignerName { get; set; } = string.Empty;

        public AlwaysAllowRule() { }

        public AlwaysAllowRule(string fileName, string signer, string signerName)
        {
            FileName = fileName;
            Signer = signer;
            SignerName = signerName;
        }

        internal bool IsValid =>
            !string.IsNullOrWhiteSpace(FileName) && !string.IsNullOrWhiteSpace(Signer);

        internal bool NameMatches(string executablePath) =>
            IsValid && string.Equals(FileName, Path.GetFileName(executablePath), StringComparison.OrdinalIgnoreCase);

        internal bool Matches(string executablePath, string signer) =>
            NameMatches(executablePath) && string.Equals(Signer, signer, StringComparison.Ordinal);

        internal bool SameRule(AlwaysAllowRule other) =>
            string.Equals(FileName, other.FileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Signer, other.Signer, StringComparison.Ordinal);

        // Adds the rule unless the same one is already there. Returns true if it was added.
        internal static bool AddTo(List<AlwaysAllowRule> rules, AlwaysAllowRule rule)
        {
            foreach (AlwaysAllowRule existing in rules)
            {
                if (existing.SameRule(rule))
                    return false;
            }
            rules.Add(rule);
            return true;
        }
    }
}
