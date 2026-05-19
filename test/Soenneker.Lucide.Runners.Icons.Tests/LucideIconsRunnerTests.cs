using Soenneker.Lucide.Runners.Icons.Abstract;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Lucide.Runners.Icons.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class LucideIconsRunnerTests : HostedUnitTest
{
    private readonly ILucideIconsRunner _runner;

    public LucideIconsRunnerTests(Host host) : base(host)
    {
        _runner = Resolve<ILucideIconsRunner>(true);
    }

    [Test]
    public void Default()
    {

    }
}
