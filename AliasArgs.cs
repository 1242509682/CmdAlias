using TShockAPI;

namespace CmdAlias;

public class AliasArgs : EventArgs
{
    public string CmdId { get; set; } = "";
    public CommandArgs Args { get; set; } = null!;
}
