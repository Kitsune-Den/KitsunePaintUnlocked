using System.Collections.Generic;

/// <summary>
/// Client-side console command that receives the server's paint ID mapping.
/// Sent by the server via NetPackageConsoleCmdClient on player connect.
/// Usage: pu_sync <base64data>
/// </summary>
public class ConsoleCmdPaintSync : ConsoleCmdAbstract
{
    public override string[] getCommands() => new[] { "pu_sync" };
    public override string getDescription() => "PaintUnlocked: apply server paint ID mapping (internal, sent automatically on connect)";
    public override bool IsExecuteOnClient => true;
    public override bool AllowedInMainMenu => false;

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        if (_params.Count < 1)
        {
            Log.Warning("[PaintUnlocked] pu_sync: no mapping data received");
            return;
        }

        PaintIdSyncManager.ApplyServerMapping(_params[0]);
    }
}
