using EventScope.App.ViewModels;
using EventScope.Core.Abstractions;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// M1a-scale smoke test for the rule the build plan makes load-bearing at M4: no
/// <c>if (broker == …)</c> in the view layer, every broker-specific element binds a
/// <see cref="SourceCapabilities"/> flag. The full audit (every capability has a bound UI
/// element, machine-checked) is M4 scope; this only proves the toolbar view model actually
/// carries the flags it will bind to rather than hardcoding them.
/// </summary>
public class CapabilityBindingTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Toolbar_reflects_whatever_capabilities_the_connected_source_reports(
        bool canPeek, bool supportsPartitions)
    {
        var toolbar = new ConnectionToolbarViewModel
        {
            CanPeekNonDestructively = canPeek,
            SupportsPartitions = supportsPartitions,
        };

        Assert.Equal(canPeek, toolbar.CanPeekNonDestructively);
        Assert.Equal(supportsPartitions, toolbar.SupportsPartitions);
    }
}
