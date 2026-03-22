using Newtonsoft.Json;
using static CmdAlias.Plugin;

namespace CmdAlias;

#region 使用条件枚举
public enum ConditionType
{
    None,   // 无条件
    Alive,  // 必须活着
    Death   // 必须死亡
}
#endregion

internal class Configuration
{
    #region 配置项成员
    [JsonProperty("使用说明", Order = 0)]
    public List<string> Text { get; set; } = new();
    [JsonProperty("命令别名表", Order = 1)]
    public List<AliasCommand> Aliases { get; set; } = new();
    #endregion

    #region 命令别名表
    public class AliasCommand
    {
        [JsonProperty("别名")]
        public string Alias { get; set; } = "";
        [JsonProperty("执行命令")]
        public List<string> Commands { get; set; } = new();
        [JsonProperty("权限")]
        public string Perms { get; set; } = "";
        [JsonProperty("越权执行")]
        public bool SetAdmin { get; set; } = false;
        [JsonProperty("帮助文本")]
        public string Help { get; set; } = "";

        [JsonProperty("冷却秒数")]
        public int CD { get; set; } = 0;
        [JsonProperty("共享冷却")]
        public bool ShareCD { get; set; } = false;
        [JsonProperty("使用条件")]
        public ConditionType Condition { get; set; } = ConditionType.None;
        [JsonProperty("补充参数")]
        public bool Supplement { get; set; } = false;
        [JsonProperty("阻止原始")]
        public bool NotSource { get; set; } = false;

        public static AliasCommand Create(string alias, string perms, string help, bool setAdmin, params string[] cmds)
        {
            return new AliasCommand { Alias = alias, Perms = perms, Help = help, Commands = cmds.ToList(), SetAdmin = setAdmin};
        }
    }
    #endregion

    #region 预设参数方法
    public void SetDefault()
    {
        Text = new List<string>{
        "本插件允许你通过配置文件自定义命令别名，支持参数替换、随机数、以其他玩家身份执行命令等功能。",
         "——————————",
        "【占位符列表】",
        "$1, $2, ... —— 替换为第 N 个参数",
        "$1-3 —— 替换为第1到第3个参数（空格连接）",
        "$2- —— 替换为从第2个参数开始到末尾的所有参数",
        "$acc —— 替换为调用者的账号名",
        "$name —— 替换为调用者的游戏内名称",
        "$random(1,10) —— 生成 min 到 max 之间的随机整数",
        "$runas(玩家名,命令) —— 以指定玩家的身份执行命令",
        "$msg(玩家名,消息) —— 向指定玩家发送私聊消息（不执行命令）",
        "——————————",
        "【配置项说明】",
        "别名 —— 玩家输入的命令名称",
        "执行命令 —— 实际执行的命令列表，可包含多条，支持上述占位符",
        "权限 —— 留空则所有人可用",
        "越权执行 —— true 为玩家越权执行特殊指令,例如Economics系列指令",
        "帮助文本 —— 当命令执行失败时显示的提示信息",
        "冷却秒数 —— 使用后需要等待的秒数（0为无冷却）",
        "共享冷却 —— true 时所有玩家共享冷却，false 时每个玩家独立冷却",
        "使用条件 —— 0 无条件, 1 必须活着, 2 必须死亡",
        "补充参数 —— true 时会将玩家输入的多余参数附加到命令末尾",
        "阻止原始 —— true 时会禁止玩家直接使用原始命令（cmdalias.bypass 权限可无视该限制）",
         "——————————",
        "输入 /reload 即可重新加载配置文件"};

        Aliases.Add(AliasCommand.Create("升级抽奖", string.Empty, string.Empty,true,
           "/give $random(1,5452) $name $random(1,11) $random(1,84)",
           "/give $random(1,5452) $name $random(1,11) $random(1,84)",
           "/give $random(1,5452) $name $random(1,11) $random(1,84)",
           "/give $random(1,5452) $name $random(1,11) $random(1,84)",
           "/give $random(1,5452) $name $random(1,11) $random(1,84)",
           "/bank deduct $name 10 魂力",
           "/me [c/8A2BE2:升级抽奖~会有什么呢][i:$random(1,5452)]"));
    }
    #endregion

    #region 读取与创建配置文件方法
    public void Write()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(Paths, json);
    }
    public static Configuration Read()
    {
        if (!File.Exists(Paths))
        {
            var NewConfig = new Configuration();
            NewConfig.SetDefault();
            NewConfig.Write();
            return NewConfig;
        }
        else
        {
            string jsonContent = File.ReadAllText(Paths);
            var config = JsonConvert.DeserializeObject<Configuration>(jsonContent)!;
            return config;
        }
    }
    #endregion
}