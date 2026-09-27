using Soenneker.Tests.HostedUnit;

namespace Soenneker.Instagram.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class InstagramOpenApiClientRunnerTests : HostedUnitTest
{
    public InstagramOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
