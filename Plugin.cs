using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Terraria;
using Microsoft.Xna.Framework;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using static CmdAlias.Configuration;
using static CmdAlias.Utils;

namespace CmdAlias;

[ApiVersion(2, 1)]
public class Plugin(Main game) : TerrariaPlugin(game)
{
    #region 插件信息
    public static string PluginName => "命令别名";
    public override string Name => PluginName;
    public override string Author => "羽学";
    public override Version Version => new(1, 0, 1);
    public override string Description => "自定义命令别名的列表，支持冷却、条件、补充参数等。";
    #endregion

    #region 文件路径
    public static readonly string Paths = Path.Combine(TShock.SavePath, $"{PluginName}.json");
    #endregion

    #region 字段
    private object randLock = new object();
    private static readonly Regex paramRegex = new Regex("\\$(\\d)(-(\\d)?)?");
    private static readonly Regex randRegex = new Regex("\\$random\\((\\d*),(\\d*)\\)", RegexOptions.IgnoreCase);
    private static readonly Regex runasRegex = new Regex("(\\$runas\\((.*?),(.*?)\\)$)", RegexOptions.IgnoreCase);
    private static readonly Regex msgRegex = new Regex("(\\$msg\\((.*?),(.*?)\\)$)", RegexOptions.IgnoreCase);
    public event EventHandler<AliasArgs> AliasExec;
    private static Dictionary<string, string> BannedSourceMap = new(); // 被禁止的原始命令 -> 对应别名（用于提示）
    #endregion

    #region 注册与释放
    public override void Initialize()
    {
        LoadConfig(); // 加载配置文件
        ParseCmds();
        GeneralHooks.ReloadEvent += ReloadConfig;
        PlayerHooks.PlayerCommand += OnPlayerCommand;  // 拦截玩家命令，处理 NotSource
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GeneralHooks.ReloadEvent -= ReloadConfig;
            PlayerHooks.PlayerCommand -= OnPlayerCommand;
            Commands.ChatCommands.RemoveAll(cmd => cmd.Names.Any(n => n.StartsWith("cmdalias.")));
        }
        base.Dispose(disposing);
    }
    #endregion

    #region 配置重载读取与写入方法
    internal static Configuration Config = new(); // 配置文件实例
    private void ReloadConfig(ReloadEventArgs args)
    {
        try
        {
            LoadConfig();
            ParseCmds();
            args.Player.SendMessage($"[{PluginName}]重新加载配置完毕。", color);
        }
        catch (Exception ex)
        {
            args.Player.SendErrorMessage("重新加载失败，请查看控制台。");
            TShock.Log.ConsoleError("无法加载配置: " + ex.Message);
        }
    }

    private static void LoadConfig()
    {
        Config = Read();
        Config.Write();

        // 重建被禁止的原始命令集合
        BannedSourceMap.Clear();
        foreach (var alias in Config.Aliases)
        {
            if (alias.NotSource && !string.IsNullOrEmpty(alias.Commands.FirstOrDefault()))
            {
                var firstCmd = alias.Commands.First().Split(' ')[0];
                if (firstCmd.StartsWith("/"))
                    firstCmd = firstCmd.Substring(1);

                // 若同一个原始命令被多个别名禁止，只保留第一个
                if (!BannedSourceMap.ContainsKey(firstCmd))
                    BannedSourceMap.Add(firstCmd, alias.Alias);
            }
        }
    }
    #endregion

    #region 解析并注册别名命令
    private void ParseCmds()
    {
        // 移除旧的别名命令
        Commands.ChatCommands.RemoveAll(cmd => cmd.Names.Any(n => n.StartsWith("cmdalias.")));

        foreach (var alias in Config.Aliases)
        {
            var cmd = new Command(alias.Perms, OnAliasCmd, alias.Alias, "cmdalias." + alias.Alias)
            {
                AllowServer = true,
                HelpText = alias.Help
            };
            Commands.ChatCommands.Add(cmd);
        }
    }
    #endregion

    #region 拦截玩家命令（处理 NotSource）
    private void OnPlayerCommand(PlayerCommandEventArgs args)
    {
        if (args.Handled) return;

        // 免禁权限：拥有 "cmdalias.bypass" 的玩家可以绕过阻止原始命令
        if (args.Player.HasPermission("cmdalias.bypass"))
            return;

        if (BannedSourceMap.TryGetValue(args.CommandName, out var aliasName))
        {
            args.Player.SendErrorMessage($"该指令已被禁止使用，请使用对应的别名 /{aliasName}");
            args.Handled = true;
        }
    }
    #endregion

    #region 别名命令回调（包含冷却、条件检查）
    private void OnAliasCmd(CommandArgs e)
    {
        string cmdId = e.Message.Split(' ').FirstOrDefault() ?? "";
        var alias = Config.Aliases.FirstOrDefault(a => a.Alias == cmdId);
        if (alias == null)
        {
            e.Player.SendErrorMessage("别名不存在或配置错误。");
            return;
        }

        // 触发事件
        AliasExec?.Invoke(null, new AliasArgs { CmdId = cmdId, Args = e });

        // 检查冷却（只有玩家，控制台无冷却）
        if (e.Player.RealPlayer)
        {
            int cdRemain = GetCooldown(e.Player.Name, alias);
            if (cdRemain > 0)
            {
                e.Player.SendErrorMessage("此指令正在冷却，还有 {0} 秒才能使用！", cdRemain);
                return;
            }

            // 检查条件
            if (!CheckCond(e.Player, alias.Condition))
            {
                e.Player.SendErrorMessage("你当前的状态无法使用此指令。");
                return;
            }
        }

        // 执行命令列表
        bool executed = DoCommands(alias, e.Player, e.Parameters);

        // 如果至少有一个命令成功执行，则更新冷却
        if (executed && e.Player.RealPlayer)
        {
            UpdateCooldown(e.Player.Name, alias);
        }
    }
    #endregion

    #region 冷却管理
    private List<CommandCD> CmdCD = new(); // 冷却列表
    private int GetCooldown(string playerName, AliasCommand alias)
    {
        if (alias.CD <= 0) return 0;

        string key = alias.ShareCD ? alias.Alias : playerName + "_" + alias.Alias;

        var record = CmdCD.FirstOrDefault(r => r.CmdKey == key);
        if (record == null) return 0;

        int elapsed = (int)(DateTime.UtcNow - record.LastTime).TotalSeconds;
        int remain = alias.CD - elapsed;
        if (remain <= 0)
        {
            CmdCD.Remove(record);
            return 0;
        }
        return remain;
    }

    private void UpdateCooldown(string playerName, AliasCommand alias)
    {
        if (alias.CD <= 0) return;

        string key = alias.ShareCD ? alias.Alias : playerName + "_" + alias.Alias;

        var record = CmdCD.FirstOrDefault(r => r.CmdKey == key);
        if (record != null)
        {
            record.LastTime = DateTime.UtcNow;
        }
        else
        {
            CmdCD.Add(new CommandCD(playerName, key));
        }
    }
    #endregion

    #region 条件检查
    private bool CheckCond(TSPlayer plr, ConditionType cond)
    {
        switch (cond)
        {
            case ConditionType.Alive:
                return plr.TPlayer.statLife > 0 && !plr.Dead;
            case ConditionType.Death:
                return plr.TPlayer.statLife <= 0 || plr.Dead;
            default:
                return true;
        }
    }
    #endregion

    #region 执行别名命令（含标记替换、参数补充）
    private bool DoCommands(AliasCommand data, TSPlayer plr, List<string> parm)
    {
        bool yes = false;

        foreach (string raw in data.Commands)
        {
            string cmd = raw;
            bool NoMess = true; // 是否要执行（$msg 会设为 false）

            // 替换参数 $1, $2- 等
            RepMarkers(parm, ref cmd);

            // 替换玩家相关占位符
            cmd = cmd.Replace("$acc", plr.Account?.Name ?? "");
            cmd = cmd.Replace("$name", plr.Name);

            // 随机数 $random(a,b)
            if (randRegex.IsMatch(cmd))
            {
                foreach (Match m in randRegex.Matches(cmd))
                {
                    int min = 0, max = 0;
                    if (int.TryParse(m.Groups[1].Value, out min) && int.TryParse(m.Groups[2].Value, out max))
                    {
                        lock (randLock)
                        {
                            cmd = cmd.Replace(m.ToString(), rand.Next(min, max).ToString());
                        }
                    }
                    else
                    {
                        TShock.Log.ConsoleError($"{m} 参数错误，已移除。");
                        cmd = cmd.Replace(m.ToString(), "");
                    }
                }
            }

            // $runas(玩家名,命令)
            if (runasRegex.IsMatch(cmd))
            {
                foreach (Match m in runasRegex.Matches(cmd))
                {
                    string targetName = m.Groups[2].Value.Trim();
                    var target = TShock.Players.FirstOrDefault(p => p?.Name == targetName);
                    if (target != null)
                    {
                        string exec = m.Groups[3].Value.Trim();
                        plr = target;
                        cmd = exec;
                    }
                }
            }

            // $msg(玩家名,消息)
            if (msgRegex.IsMatch(cmd))
            {
                foreach (Match m in msgRegex.Matches(cmd))
                {
                    string targetName = m.Groups[2].Value.Trim();
                    string msg = m.Groups[3].Value.Trim();
                    var target = TShock.Players.FirstOrDefault(p => p?.Name == targetName);
                    if (target != null)
                    {
                        NoMess = false; // 不执行命令，只发送消息
                        target.SendInfoMessage(msg);
                    }
                }
            }

            // 参数补充模式：将多余的参数附加到末尾
            if (data.Supplement && parm.Count > 0)
            {
                cmd += " " + string.Join(" ", parm);
            }

            try
            {
                // 防止自调用导致无限循环
                string firstWord = cmd.Split(' ')[0];
                if (firstWord.StartsWith("/") && firstWord.Substring(1).Equals(data.Alias, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                if (NoMess)
                {
                    InvokeCmd(plr, cmd, data.SetAdmin);
                    yes = true;
                }
            }
            catch
            {
                plr.SendErrorMessage(data.Help);
            }
        }

        return yes;
    }
    #endregion

    #region 标记替换（参数）
    private void RepMarkers(IList<string> parm, ref string cmd)
    {
        if (!paramRegex.IsMatch(cmd))
            return;

        foreach (Match m in paramRegex.Matches(cmd))
        {
            int start = 0, end = 0;
            bool isRange = !string.IsNullOrEmpty(m.Groups[2].Value);

            if (int.TryParse(m.Groups[1].Value, out start))
            {
                if (!isRange)
                {
                    // $num
                    cmd = cmd.Replace(m.ToString(), start <= parm.Count ? parm[start - 1] : "");
                }
                else
                {
                    // $start-end 或 $start-
                    string endVal = m.Groups[3].Value;
                    if (!string.IsNullOrEmpty(endVal) && int.TryParse(endVal, out end))
                    {
                        // $start-end
                        var sb = new StringBuilder();
                        for (int i = start; i <= end && i <= parm.Count; i++)
                            sb.Append(" " + parm[i - 1]);
                        cmd = cmd.Replace(m.ToString(), sb.ToString().TrimStart());
                    }
                    else
                    {
                        // $start-
                        var sb = new StringBuilder();
                        for (int i = start; i <= parm.Count; i++)
                            sb.Append(" " + parm[i - 1]);
                        cmd = cmd.Replace(m.ToString(), sb.ToString().TrimStart());
                    }
                }
            }
        }
    }
    #endregion

    #region 无权限执行命令
    public static bool InvokeCmd(TSPlayer plr, string text, bool setAdmin)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        string cmdText = text.Remove(0, 1); // 去掉开头的 /
        MethodInfo method = typeof(Commands).GetMethod("ParseParameters", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (method == null)
            return false;

        var args = (List<string>)method.Invoke(null, [cmdText])!;
        if (args == null || args.Count < 1)
            return false;

        string cmdName = args[0].ToLower();
        args.RemoveAt(0);

        var cmds = Commands.ChatCommands.Where(c => c.HasAlias(cmdName)).ToList();
        if (cmds.Count == 0)
        {
            if (plr.AwaitingResponse.ContainsKey(cmdName))
            {
                Action<object> action = plr.AwaitingResponse[cmdName];
                plr.AwaitingResponse.Remove(cmdName);
                action(new CommandArgs(cmdText, plr, args));
                return true;
            }
            plr.SendErrorMessage("输入的命令无效。请输入 /help 以获取有效命令列表");
            return true;
        }

        foreach (Command cmd in cmds)
        {
            if (!cmd.AllowServer && !plr.RealPlayer)
            {
                plr.SendErrorMessage("你必须在游戏中使用这个命令");
                continue;
            }

            if (cmd.DoLog)
            {
                TShock.Utils.SendLogs(plr.Name + " 执行: /" + cmdText + ".", Color.Red);
            }

            if (setAdmin)
            {
                var group = plr.Group;
                try
                {
                    plr.Group = new SuperAdminGroup();
                    cmd.CommandDelegate(new CommandArgs(cmdText, plr, args));
                }
                finally
                {
                    plr.Group = group;
                }
            }
            else
            {
                cmd.CommandDelegate(new CommandArgs(cmdText, plr, args));
            }
        }
        return true;
    }
    #endregion
}