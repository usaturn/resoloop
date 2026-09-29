using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private const string SavedAssetWorld = """
        {"schemaVersion":"1","ownership":{"key":"saved-assets"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
         "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
         "children":[
           {"slot":{"key":"crate-a","name":"CrateA"},"components":[{"key":"mesh-a","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]},
           {"slot":{"key":"crate-b","name":"CrateB"},"components":[{"key":"mesh-b","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]}]}
        """;

    [Fact]
    public async Task AssetUrlMigratedByWorldSaveIsAdoptedInsteadOfRewrittenToLocal()
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-migrated");
        SaveAssetUrls(client, "resdb:///saved-crate", "resdb:///saved-crate");
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        var asset = Assert.Single(plan.Operations, operation => operation.Kind == "asset");
        Assert.Equal("no-op", asset.Action);
        Assert.Contains("migrated to resdb by a world save", asset.Reason);
        Assert.Empty(plan.Changes);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
        Assert.Equal(0, client.Writes);

        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
        Assert.Equal(1, client.AssetImports);
        var saved = StateAsset(state, "mesh");
        Assert.Equal("resdb:///saved-crate", saved["url"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "crate.bin")))),
            saved["sourceHash"]!.GetValue<string>());
        Assert.Equal("mesh", saved["kind"]!.GetValue<string>());

        var again = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, again.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
        Assert.Equal(1, client.AssetImports);
        var replanned = await service.PlanApplyAsync(document, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
    }

    [Fact]
    public async Task AssetUrlMigratedByWorldSaveIsAdoptedWhenItsOnlyReferenceIsRelocated()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var initial = ReloadDocument("saved-relocated", """
            {"schemaVersion":"1","ownership":{"key":"saved-relocated"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
             "children":[
               {"slot":{"key":"crate-a","name":"CrateA"},"components":[{"key":"mesh-a","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]},
               {"slot":{"key":"crate-b","name":"CrateB"}}]}
            """);
        var moved = ReloadDocument("saved-relocated-moved", """
            {"schemaVersion":"1","ownership":{"key":"saved-relocated"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
             "children":[
               {"slot":{"key":"crate-a","name":"CrateA"}},
               {"slot":{"key":"crate-b","name":"CrateB"},"components":[{"key":"mesh-a","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]}]}
            """);
        var client = new FakeResoniteClient(initial) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "saved-relocated.state.json");
        await service.ApplyAsync(initial, new ApplyOptions(state));
        var holder = Assert.Single(Holders(client));
        holder.Members["URL"] = holder.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-crate") };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state));

        var asset = Assert.Single(plan.Operations, operation => operation.Kind == "asset");
        Assert.Equal("no-op", asset.Action);
        Assert.Contains("migrated to resdb by a world save", asset.Reason);
        Assert.Equal("relocate", Assert.Single(plan.Operations, operation => operation.Kind == "component").Action);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));

        var applied = await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(1, applied.ComponentsDeleted);
        Assert.Equal(1, client.AssetImports);
        Assert.Equal("resdb:///saved-crate", Assert.Single(HolderUrls(client)));
        Assert.Equal("resdb:///saved-crate", StateAsset(state, "mesh")["url"]!.GetValue<string>());

        client.ResetWriteCounts();
        await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        var replanned = await service.PlanApplyAsync(moved, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
    }

    [Theory]
    [InlineData("resdb:///saved-a", "resdb:///saved-b")]
    [InlineData("resdb:///saved-a", null)]
    [InlineData("local://other/crate.meshx", "local://other/crate.meshx")]
    [InlineData("https://example.invalid/crate.meshx", "https://example.invalid/crate.meshx")]
    public async Task AssetUrlIsNotTreatedAsMigratedUnlessEveryLiveReferenceSharesOneResdbUri(string first, string? second)
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-mixed-" + Guid.NewGuid().ToString("N")[..8]);
        SaveAssetUrls(client, first, second);

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        var asset = Assert.Single(plan.Operations, operation => operation.Kind == "asset");
        Assert.Equal("source hash and imported URL match state", asset.Reason);
        Assert.Contains(plan.Changes, operation => operation.Kind == "component" && operation.Action == "update");
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.True(applied.ComponentsUpdated > 0);
        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.All(HolderUrls(client), url => Assert.Equal("local://machine/asset-1", url));
    }

    [Fact]
    public async Task ChangedAssetSourceIsReimportedEvenAfterAWorldSaveMigratedItsUrl()
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-changed");
        SaveAssetUrls(client, "resdb:///saved-crate", "resdb:///saved-crate");
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "second crate");

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("create", Assert.Single(plan.Operations, operation => operation.Kind == "asset").Action);
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(2, client.AssetImports);
        Assert.Equal(2, applied.ComponentsUpdated);
        Assert.Equal("local://machine/asset-2", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.All(HolderUrls(client), url => Assert.Equal("local://machine/asset-2", url));
    }

    [Fact]
    public async Task AssetWithoutLiveReferencesKeepsItsStateUrl()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var document = ReloadDocument("saved-unreferenced", """
            {"schemaVersion":"1","ownership":{"key":"saved-unreferenced"},"slot":{"key":"root","name":"Unreferenced","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}}}
            """);
        var client = new FakeResoniteClient(document) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "saved-unreferenced.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("source hash and imported URL match state", Assert.Single(plan.Operations, operation => operation.Kind == "asset").Reason);
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal(0, client.Writes);
    }

    private async Task<(ApplyDocument Document, FakeResoniteClient Client, WorldService Service, string State)> ApplySavedAssetWorldAsync(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var document = ReloadDocument(name, SavedAssetWorld);
        var client = new FakeResoniteClient(document) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.All(HolderUrls(client), url => Assert.Equal("local://machine/asset-1", url));
        return (document, client, service, state);
    }

    // Simulates Resonite moving the imported local asset into the saved world: live URLs change, state does not.
    private static void SaveAssetUrls(FakeResoniteClient client, string first, string? second)
    {
        var holders = Holders(client);
        if (first is not null) holders[0].Members["URL"] = holders[0].Members["URL"] with { Value = JsonValue.Create(first) };
        if (second is not null) holders[1].Members["URL"] = holders[1].Members["URL"] with { Value = JsonValue.Create(second) };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
    }

    private static FakeResoniteClient.FakeComponent[] Holders(FakeResoniteClient client) =>
        client.Root.Children.Single().Children.SelectMany(slot => slot.Components)
            .Where(component => component.Type == "Test.AssetHolder").ToArray();

    private static string[] HolderUrls(FakeResoniteClient client) =>
        Holders(client).Select(component => component.Members["URL"].Value!.GetValue<string>()).ToArray();

    private static JsonNode StateAsset(string state, string key) =>
        JsonNode.Parse(File.ReadAllText(state))!["assets"]![key]!;
}
