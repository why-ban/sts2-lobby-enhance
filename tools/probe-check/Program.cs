using Sts2LobbyEnhance;

// 独立校验 CompatProbe 的真实判定结果。
// 这个探测是整个补丁的开关：判错会导致补丁在旧版本上误启动，或在新版本上不生效。

CompatSnapshot snapshot = CompatProbe.Capture();
Console.WriteLine("snapshot : " + snapshot.Describe());
Console.WriteLine();

(int Index, string Name, bool Value)[] rows =
{
    (1, "InitialGameInfoMessage.gameMode 消失", snapshot.InitialGameInfoLostGameMode),
    (2, "ClientLobbyJoinRequestMessage.maxAscensionUnlocked 消失", snapshot.JoinRequestLostMaxAscensionUnlocked),
    (3, "NetHostGameService.DisconnectClient 变 4 参", snapshot.DisconnectClientGainedParameter),
    (4, "NetHostGameService.ConnectedPeers 不再是 List<>", snapshot.ConnectedPeersChangedType),
    (5, "RelicPickingResult.GenerateRelicFight 变 4 参", snapshot.GenerateRelicFightGainedParameter),
    (6, "SaveManager.UpdateProgressWithRunData 有返回值", snapshot.UpdateProgressGainedReturnValue)
};

foreach ((int index, string name, bool value) in rows)
{
    Console.WriteLine($"  #{index} {(value ? "TRUE " : "FALSE")}  {name}");
}

int trueCount = rows.Count(row => row.Value);
Console.WriteLine();
// 没有「总开关」这个概念了：每个修复项用 ICompatFix.Applies 自己判断。这里只回答
// 「这个构建露出几处 0.113 签名变化」，6/6 表示整套兼容修复的前提都在。
Console.WriteLine($"结论: {trueCount}/6 命中 -> {(trueCount == 6 ? "六处断点全在，兼容修复的前提成立" : "有断点不成立，相关修复项会各自跳过")}");
return trueCount == 6 ? 0 : 2;
