using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace GoldenPick;

// forge-compliant: GUID lowercase reverse-domain, must match the BepInEx plugin GUID.
// Name + Author are letters/numbers only. Version is read from the assembly so it
// only gets bumped in one place (Directory.Build.props at the repo root).
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.manimal.goldenpick";
    public override string Name { get; init; } = "GoldenPick";
    public override string Author { get; init; } = "Manimal";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("~2.0.20") }
    };
    public override string? Url { get; init; } = "";
    public override bool? IsBundleMod { get; init; } = true;
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class GoldenPickServer(
    WTTServerCommonLib.WTTServerCommonLib wttCommon) : IOnLoad
{
    public async Task OnLoad()
    {
        var assembly = Assembly.GetExecutingAssembly();
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);

        // patch SPT's ItemExtensions.ReplaceIDs so our golden-crate/pick keeps its original Id
        // through the mail flow. without this, MailSendService regenerates ids → the crate's
        // /goldenpick/cratesig record lookup (keyed by the minted id) misses, and the pick
        // metadata keyed by that id no longer resolves.
        RaidProgress.PreserveGoldenCrateIdPatch.Apply();
    }
}
