using System.Reflection;
using System.Runtime.CompilerServices;
using PhoneBox.Agent;

/// <summary>
/// The catalog (widgets.json) gets its "terminal" entry together with the phone's widget, in a later change of this
/// series; until then it does not list the type, and ConfigStore drops widgets the catalog does not know. The terminal
/// tests need a terminal widget on a page, so the type is added here once, when the test assembly loads and before any
/// test reads the catalog. This file goes away when widgets.json lists "terminal".
/// </summary>
internal static class TerminalTestCatalog
{
    [ModuleInitializer]
    internal static void CatalogKnowsTerminal()
    {
        var sizes = (Dictionary<string, (int, int)>)typeof(Catalog).GetField("Sizes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        sizes.TryAdd("terminal", (2, 1));
    }
}
