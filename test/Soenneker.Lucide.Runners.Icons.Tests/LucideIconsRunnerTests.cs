using System;
using Soenneker.Lucide.Runners.Icons.Utils.Abstract;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Lucide.Runners.Icons.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class LucideIconsRunnerTests : HostedUnitTest
{
    private readonly IFileOperationsUtil _fileOperationsUtil;

    public LucideIconsRunnerTests(Host host) : base(host)
    {
        _fileOperationsUtil = Resolve<IFileOperationsUtil>(true);
    }

    [Test]
    public void Default()
    {
        if (_fileOperationsUtil is null)
            throw new InvalidOperationException("Could not resolve file operations util");
    }
}
