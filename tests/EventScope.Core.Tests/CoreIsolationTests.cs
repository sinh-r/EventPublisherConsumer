using EventScope.Core.Abstractions;
using Xunit;

namespace EventScope.Core.Tests;

/// <summary>
/// EventScope.Core must stay free of broker SDKs and UI frameworks so it can
/// be referenced and unit-tested without either. This test must fail the
/// moment someone adds a stray `using Confluent.Kafka;` to Core.
/// </summary>
public class CoreIsolationTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Confluent.Kafka",
        "Azure.Messaging",
        "AWSSDK",
        "Avalonia",
    ];

    [Fact]
    public void Core_assembly_references_no_broker_or_ui_assemblies()
    {
        var coreAssembly = typeof(IEventSource).Assembly;
        var referenced = coreAssembly.GetReferencedAssemblies();

        var violations = referenced
            .Where(a => ForbiddenAssemblyPrefixes.Any(p =>
                a.Name is not null && a.Name.StartsWith(p, StringComparison.Ordinal)))
            .Select(a => a.Name)
            .ToList();

        Assert.True(violations.Count == 0,
            $"EventScope.Core referenced forbidden assemblies: {string.Join(", ", violations)}");
    }
}
