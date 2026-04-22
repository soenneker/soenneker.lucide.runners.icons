using Soenneker.Tests.HostedUnit;

namespace Soenneker.Lucide.Runners.Icons.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class LucideIconsRunnerTests : HostedUnitTest
{
    public LucideIconsRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
