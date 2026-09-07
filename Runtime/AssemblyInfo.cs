using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Cuvara.Netcode.Tests.Editor")]

// The live measurement reports WorldViewBinder.TargetLeadTicks(), which is internal. It is
// a diagnostic there, not an assertion target: the number steers the prediction clock, and a
// report that shows every symptom of a wrong clock offset while omitting the offset itself is
// what made the same defect get diagnosed as a tick-rate mismatch twice.
[assembly: InternalsVisibleTo("Cuvara.Netcode.Tests.PlayMode")]
