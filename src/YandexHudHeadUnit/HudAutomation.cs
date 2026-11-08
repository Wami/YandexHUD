namespace YandexHudHeadUnit;

// Commands exposed to automation tools (Tasker and similar) and to the in-app
// controls. Pure logic so the host tests can verify action parsing.
internal enum HudCommand
{
    None,
    Connect,
    Disconnect,
    Exit,
    Open,
}

internal static class HudAutomation
{
    internal static string? Action(string packageName, HudCommand command) => command switch
    {
        HudCommand.Connect => packageName + ".CONNECT",
        HudCommand.Disconnect => packageName + ".DISCONNECT",
        HudCommand.Exit => packageName + ".EXIT",
        HudCommand.Open => packageName + ".OPEN",
        _ => null,
    };

    internal static HudCommand Parse(string? packageName, string? action)
    {
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(action))
            return HudCommand.None;
        string value = action.Trim();
        foreach (HudCommand command in new[]
                 { HudCommand.Connect, HudCommand.Disconnect, HudCommand.Exit, HudCommand.Open })
        {
            if (string.Equals(value, Action(packageName, command), StringComparison.OrdinalIgnoreCase))
                return command;
        }
        return HudCommand.None;
    }
}
