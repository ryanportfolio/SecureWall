using System.Text.Json.Serialization.Metadata;

// The real ExceptionPolicy is linked into this harness to test merge semantics.
// Its JSON converter and RuleListPolicy reference these types; merge tests never
// serialize and never inspect rule contents.
namespace pylorak.TinyWall
{
    public class RuleDef
    {
    }

    internal sealed class SourceGenerationContext
    {
        internal static SourceGenerationContext Default { get; } = new();

        internal JsonTypeInfo<HardBlockPolicy> HardBlockPolicy => throw Forbidden();
        internal JsonTypeInfo<UnrestrictedPolicy> UnrestrictedPolicy => throw Forbidden();
        internal JsonTypeInfo<TcpUdpPolicy> TcpUdpPolicy => throw Forbidden();
        internal JsonTypeInfo<RuleListPolicy> RuleListPolicy => throw Forbidden();
        internal JsonTypeInfo<GlobalSubject> GlobalSubject => throw Forbidden();
        internal JsonTypeInfo<AppContainerSubject> AppContainerSubject => throw Forbidden();
        internal JsonTypeInfo<ExecutableSubject> ExecutableSubject => throw Forbidden();
        internal JsonTypeInfo<ServiceSubject> ServiceSubject => throw Forbidden();
        internal JsonTypeInfo<FirewallExceptionV3> FirewallExceptionV3 => throw Forbidden();
        internal JsonTypeInfo<ServerConfiguration> ServerConfiguration => throw Forbidden();

        private static InvalidOperationException Forbidden() =>
            new("Policy and subject serialization is outside the core merge tests.");
    }
}
