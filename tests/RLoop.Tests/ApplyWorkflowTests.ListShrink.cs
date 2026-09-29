using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    // ResoniteLink 0.13.1 cannot remove list elements: an update only replaces leading elements. A declared list that is
    // shorter than the runtime list can only converge by recreating the Component.
    private const string Identity = ""","identityFields":["Label"]""";

    private ApplyDocument ShrinkDocument(string name, int materials, string rendererExtra = "", string extraComponents = "")
    {
        var materialComponents = string.Join(",", Enumerable.Range(1, 4).Select(i =>
            $$"""{"key":"m{{i}}","type":"Test.Material","fields":{"Tint":{{i}} } }"""));
        var list = string.Join(",", Enumerable.Range(1, materials).Select(i => $"\"$ref:m{i}\""));
        return ReloadDocument(name, $$"""
            {"schemaVersion":"1","ownership":{"key":"{{name}}"},"slot":{"key":"root","name":"Plaza","parent":"Root"},
             "components":[{{materialComponents}}],
             "children":[{"slot":{"key":"rubble","name":"Rubble"},
               "components":[{"key":"renderer","type":"Test.Renderer","fields":{"Materials":[{{list}}],"Label":"north"}{{rendererExtra}} }{{extraComponents}}]}]}
            """);
    }

    private static FakeResoniteClient.FakeSlot Rubble(FakeResoniteClient client) =>
        client.Root.Children.Single(slot => slot.Name == "Plaza").Children.Single();

    private static FakeResoniteClient.FakeComponent Renderer(FakeResoniteClient client) =>
        Assert.Single(Rubble(client).Components, component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == "north");

    private static string[] MaterialTargets(FakeResoniteClient.FakeComponent renderer)
    {
        var member = renderer.Members["Materials"];
        Assert.Equal("list", member.Kind);
        return member.Elements!.Select(element => element.TargetId!).ToArray();
    }

    private static string[] MaterialIds(FakeResoniteClient client, int count) =>
        client.Root.Children.Single(slot => slot.Name == "Plaza").Components.Take(count).Select(component => component.Id).ToArray();

    private static JsonNode StateComponent(string state, string key) =>
        JsonNode.Parse(File.ReadAllText(state))!["components"]![key]!;

    private async Task<(FakeResoniteClient Client, WorldService Service, string State, string OldId)> ApplyFourMaterialsAsync(
        string name, string rendererExtra = "", string extraComponents = "")
    {
        var four = ShrinkDocument(name, 4, rendererExtra, extraComponents);
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 4), MaterialTargets(renderer));
        client.ResetWriteCounts();
        return (client, service, state, renderer.Id);
    }

    [Fact]
    public async Task FakeListMemberKeepsTrailingElementsLikeTheRuntime()
    {
        var (client, _, _, oldId) = await ApplyFourMaterialsAsync("shrink-fake");
        var ids = MaterialIds(client, 4);
        await client.SetComponentMembersAsync(oldId, "Test.Renderer",
            new Dictionary<string, string> { ["Materials"] = $"[\"{ids[1]}\"]" });
        Assert.Equal([ids[1], ids[1], ids[2], ids[3]], MaterialTargets(Renderer(client)));
        await client.SetComponentMembersAsync(oldId, "Test.Renderer", new Dictionary<string, string> { ["Materials"] = "[]" });
        Assert.Equal(4, MaterialTargets(Renderer(client)).Length);
    }

    [Fact]
    public async Task ListMemberShrinkRecreatesComponentAndReapplyConverges()
    {
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink");
        var three = ShrinkDocument("shrink", 3);
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var plan = await service.PlanApplyAsync(three, new ApplyOptions(state));

        var entry = Assert.Single(plan.Changes);
        Assert.Equal("renderer", entry.Key);
        Assert.Equal("recreate", entry.Action);
        Assert.Contains("list member Materials shrinks from 4 to 3", entry.Reason);
        Assert.Contains("recreated", entry.Reason);
        Assert.Equal(1, plan.Updates);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
        Assert.Equal(0, client.Writes);

        var applied = await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(1, applied.ComponentsDeleted);
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);

        client.ResetWriteCounts();
        var replanned = await service.PlanApplyAsync(three, new ApplyOptions(state));
        var again = await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
        Assert.Equal(0, again.ComponentsUpdated + again.ComponentsAdded + again.ComponentsDeleted);
        Assert.Equal(0, client.Writes);
        Assert.Equal(renderer.Id, Renderer(client).Id);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    public async Task ListMemberThatDoesNotShrinkIsStillUpdatedInPlace(int before, int after)
    {
        var name = $"shrink-grow-{before}-{after}";
        var initial = ShrinkDocument(name, before);
        var client = new FakeResoniteClient(initial);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(initial, new ApplyOptions(state));
        var oldId = Renderer(client).Id;
        if (before == after)
            Renderer(client).Members["Materials"] = Renderer(client).Members["Materials"] with
            {
                Elements = Renderer(client).Members["Materials"].Elements!.Reverse().ToArray()
            };
        client.ResetWriteCounts();
        var next = ShrinkDocument(name, after);

        var plan = await service.PlanApplyAsync(next, new ApplyOptions(state));
        var applied = await service.ApplyAsync(next, new ApplyOptions(state));

        Assert.Equal("update", Assert.Single(plan.Changes).Action);
        Assert.Equal(1, applied.ComponentsUpdated);
        Assert.Equal(0, applied.ComponentsAdded + applied.ComponentsDeleted);
        Assert.Equal(oldId, Renderer(client).Id);
        Assert.Equal(MaterialIds(client, after), MaterialTargets(Renderer(client)));
    }

    [Theory]
    [InlineData("component")]
    [InlineData("member")]
    public async Task ListShrinkOfComponentReferencedFromOutsideManagementStopsBeforeMutation(string target)
    {
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-external-" + target);
        var targetId = target == "component" ? oldId : Renderer(client).Members["Materials"].Id!;
        var external = client.PrependComponent(client.Root, "Test.Source", new Dictionary<string, string> { ["Target"] = targetId });
        var three = ShrinkDocument("shrink-external-" + target, 3);
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(three, new ApplyOptions(state)));
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_REFERENCED", planError.Code);
        Assert.Equal("APPLY_LIST_SHRINK_REFERENCED", applyError.Code);
        Assert.Contains(external.Id, System.Text.Json.JsonSerializer.Serialize(applyError.Context));
        Assert.Equal(0, client.Writes);
        Assert.Equal(oldId, Renderer(client).Id);
        Assert.Equal(4, MaterialTargets(Renderer(client)).Length);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
    }

    [Fact]
    public async Task ListShrinkRepointsManagedReferencesToTheRecreatedComponent()
    {
        const string source = """,{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-managed", extraComponents: source);
        var sourceComponent = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Source");
        Assert.Equal(oldId, sourceComponent.Members["Target"].TargetId);
        var three = ShrinkDocument("shrink-managed", 3, extraComponents: source);

        var plan = await service.PlanApplyAsync(three, new ApplyOptions(state));
        await service.ApplyAsync(three, new ApplyOptions(state));

        Assert.Equal("recreate", Assert.Single(plan.Changes, entry => entry.Key == "renderer").Action);
        Assert.Equal("update", Assert.Single(plan.Changes, entry => entry.Key == "source").Action);
        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(renderer.Id, sourceComponent.Members["Target"].TargetId);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("before-add")]
    [InlineData("lost-add-response")]
    [InlineData("before-remove")]
    public async Task InterruptedListShrinkRecreateResumesWithoutDuplicates(string failure)
    {
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-resume-" + failure);
        var three = ShrinkDocument("shrink-resume-" + failure, 3);
        if (failure == "lost-add-response") client.LoseNextComponentCreateResponse = true;
        else client.FailOnWrite = failure == "before-add" ? 1 : 2;

        var error = await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.NotNull(error);
        client.FailOnWrite = null;
        var renderers = Rubble(client).Components.Where(component => component.Type == "Test.Renderer").ToArray();
        Assert.Equal(failure == "before-add" ? 1 : 2, renderers.Length);
        Assert.Equal(oldId, StateComponent(state, "renderer")["supersededId"]!.GetValue<string>());

        var resumePlan = await service.PlanApplyAsync(three, new ApplyOptions(state));
        Assert.Equal("recreate", Assert.Single(resumePlan.Changes).Action);
        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecreatedComponentKeepsConsistentIdentityAndIndexAcrossWorldReload(bool identity)
    {
        var name = "shrink-identity-" + identity;
        var sibling = identity
            ? """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"south"},"identityFields":["Label"]}"""
            : """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"south"}}""";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name, identity ? Identity : "", sibling);
        var three = ShrinkDocument(name, 3, identity ? Identity : "", sibling);

        await service.ApplyAsync(three, new ApplyOptions(state));

        var components = Rubble(client).Components;
        var recreated = components.Single(component => component.Type == "Test.Renderer" && component.Members["Label"].Value!.GetValue<string>() == "north");
        Assert.NotEqual(oldId, recreated.Id);
        foreach (var key in new[] { "renderer", "sibling" })
        {
            var saved = StateComponent(state, key);
            Assert.Equal(components.FindIndex(component => component.Id == saved["id"]!.GetValue<string>()),
                saved["componentIndex"]!.GetValue<int>());
            Assert.Equal(key == "renderer" ? 0 : 1, saved["typeOrdinal"]!.GetValue<int>());
            if (identity) Assert.Equal(key == "renderer" ? "north" : "south", saved["identityValues"]!["Label"]!.GetValue<string>());
        }

        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        var replanned = await service.PlanApplyAsync(three, new ApplyOptions(state));
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
        Assert.Equal(0, client.Writes);
    }

    // A reload renumbers both halves of an interrupted recreate, so supersededId no longer identifies the old Component.
    // Without identityFields the index tie-break would pick the old one, recreate again, and orphan the replacement.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedListShrinkRecreateStopsBeforeMutationAfterWorldReload(bool identity)
    {
        var name = "shrink-reload-" + identity;
        var (client, service, state, _) = await ApplyFourMaterialsAsync(name, identity ? Identity : "");
        var three = ShrinkDocument(name, 3, identity ? Identity : "");
        client.FailOnWrite = 2;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        var renderers = Rubble(client).Components.Where(component => component.Type == "Test.Renderer").Select(component => component.Id).ToArray();
        Assert.Equal(2, renderers.Length);
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(three, new ApplyOptions(state)));
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", planError.Code);
        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", applyError.Code);
        var context = System.Text.Json.JsonSerializer.Serialize(applyError.Context);
        Assert.All(renderers, id => Assert.Contains(id, context));
        Assert.Equal(0, client.Writes);
        Assert.Equal(renderers, Rubble(client).Components.Where(component => component.Type == "Test.Renderer").Select(component => component.Id));
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
    }

    [Fact]
    public async Task RecreatedComponentAdoptsAnAssetUrlMigratedByAWorldSave()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        ApplyDocument Build(int materials)
        {
            var json = JsonNode.Parse(File.ReadAllText(ShrinkDocument("shrink-asset", materials).SourcePath!))!;
            json["assets"] = JsonNode.Parse("""{"mesh":{"kind":"mesh","source":"crate.bin"}}""");
            json["children"]![0]!["components"]![0]!["fields"]!["URL"] = "$asset:mesh";
            return ReloadDocument("shrink-asset", json.ToJsonString());
        }
        var four = Build(4);
        var client = new FakeResoniteClient(four) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "shrink-asset.state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        var renderer = Renderer(client);
        renderer.Members["URL"] = renderer.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-crate") };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
        var three = Build(3);

        var plan = await service.PlanApplyAsync(three, new ApplyOptions(state));
        await service.ApplyAsync(three, new ApplyOptions(state));

        Assert.Contains("migrated to resdb", Assert.Single(plan.Operations, operation => operation.Kind == "asset").Reason);
        Assert.Equal("resdb:///saved-crate", Renderer(client).Members["URL"].Value!.GetValue<string>());
        Assert.Equal("resdb:///saved-crate", JsonNode.Parse(File.ReadAllText(state))!["assets"]!["mesh"]!["url"]!.GetValue<string>());
        Assert.Equal(1, client.AssetImports);
    }

    [Fact]
    public async Task RecreateThatTheRuntimeRefillsStopsInsteadOfRecreatingOnEveryApply()
    {
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-refill");
        var three = ShrinkDocument("shrink-refill", 3);
        client.AfterComponentAdded = component =>
        {
            if (component.Type != "Test.Renderer") return;
            var materials = component.Members["Materials"];
            component.Members["Materials"] = materials with
            {
                Elements = [.. materials.Elements!, new MemberValue("reference", component.Id + ":Materials[3]", TargetId: "null")]
            };
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
            Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
            Assert.Contains("Materials", System.Text.Json.JsonSerializer.Serialize(error.Context));
            Assert.Equal(oldId, Renderer(client).Id);
            Assert.Equal(oldId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
            Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        }
    }
}
