using Avalonia;
using Avalonia.Markup.Xaml;

namespace EventScope.App.Tests;

public partial class HeadlessTestApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
