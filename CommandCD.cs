namespace CmdAlias;

#region 冷却记录类
public class CommandCD
{
    public string Name { get; set; }      // 玩家名
    public string CmdKey { get; set; }     // 命令标识（别名或源命令）
    public DateTime LastTime { get; set; }
    public CommandCD(string name, string cmdKey)
    {
        Name = name;
        CmdKey = cmdKey;
        LastTime = DateTime.UtcNow;
    }
}
#endregion
