using Marketgrade.Engine;

namespace Marketgrade.Engine.Tests;

public class EngineDependencyTests
{
    [Fact]
    public void Engine_does_not_reference_AspNetCore()
    {
        var webReferences = typeof(EngineInfo).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null && name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(webReferences);
    }
}
