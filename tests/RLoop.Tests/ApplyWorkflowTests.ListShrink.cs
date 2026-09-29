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

    // The renderer of ShrinkDocument, with three materials, moved to a child Slot of Rubble or no longer declared.
    private ApplyDocument RendererElsewhereDocument(string name, bool moved)
    {
        var materialComponents = string.Join(",", Enumerable.Range(1, 4).Select(i =>
            $$"""{"key":"m{{i}}","type":"Test.Material","fields":{"Tint":{{i}} } }"""));
        var debris = moved
            ? ""","children":[{"slot":{"key":"debris","name":"Debris"},"components":[{"key":"renderer","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3"],"Label":"north"}}]}]"""
            : "";
        return ReloadDocument(name, $$"""
            {"schemaVersion":"1","ownership":{"key":"{{name}}"},"slot":{"key":"root","name":"Plaza","parent":"Root"},
             "components":[{{materialComponents}}],
             "children":[{"slot":{"key":"rubble","name":"Rubble"},"components":[]{{debris}} }]}
            """);
    }

    private static FakeResoniteClient.FakeSlot Rubble(FakeResoniteClient client) =>
        client.Root.Children.Single(slot => slot.Name == "Plaza").Children.Single();

    private static string[] RendererIds(FakeResoniteClient.FakeSlot slot) =>
        slot.Components.Where(component => component.Type == "Test.Renderer").Select(component => component.Id).ToArray();

    // Simulates runtime logic that sizes a new renderer's list itself, one element longer than any declaration.
    private static void RefillRenderers(FakeResoniteClient client) => client.AfterComponentAdded = component =>
    {
        if (component.Type != "Test.Renderer") return;
        var materials = component.Members["Materials"];
        component.Members["Materials"] = materials with
        {
            Elements = [.. materials.Elements!, new MemberValue("reference", component.Id + ":Materials[3]", TargetId: "null")]
        };
    };

    // Stops the recreate after the replacement exists and before the replaced Component is removed.
    private static async Task InterruptBeforeRemovingReplacedAsync(FakeResoniteClient client, WorldService service, ApplyDocument document, string state)
    {
        client.FailOnWrite = 2;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        client.FailOnWrite = null;
        client.ResetWriteCounts();
    }

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

    // Until the replacement exists its key has no saved ID, so resolving it again must not bind a same-type sibling that
    // another key holds. Otherwise apply stops on an ownership conflict however often it is re-run.
    [Theory]
    [InlineData("before-add")]
    [InlineData("lost-add-response")]
    public async Task InterruptedRecreateNextToASameTypeSiblingResumes(string failure)
    {
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"south"}}""";
        var name = "shrink-resume-sibling-" + failure;
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name, extraComponents: sibling);
        var siblingId = StateComponent(state, "sibling")["id"]!.GetValue<string>();
        var three = ShrinkDocument(name, 3, extraComponents: sibling);
        if (failure == "lost-add-response") client.LoseNextComponentCreateResponse = true;
        else client.FailOnWrite = 1;

        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        client.FailOnWrite = null;
        var plan = await service.PlanApplyAsync(three, new ApplyOptions(state));
        await service.ApplyAsync(three, new ApplyOptions(state));

        Assert.Equal("recreate", Assert.Single(plan.Changes).Action);
        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([siblingId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // The same holds for a new key declared next to a managed Component of its type.
    [Fact]
    public async Task InterruptedCreateOfANewKeyNextToAManagedSameTypeComponentResumes()
    {
        const string extra = """,{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        var (client, service, state, rendererId) = await ApplyFourMaterialsAsync("new-key-resume");
        var withExtra = ShrinkDocument("new-key-resume", 4, extraComponents: extra);
        client.FailOnWrite = 1;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));
        client.FailOnWrite = null;
        await service.ApplyAsync(withExtra, new ApplyOptions(state));

        var added = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Renderer" && component.Id != rendererId);
        Assert.Equal(rendererId, Renderer(client).Id);
        Assert.Equal(rendererId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(added.Id, StateComponent(state, "extra")["id"]!.GetValue<string>());
        client.ResetWriteCounts();
        await service.ApplyAsync(withExtra, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // A same-type Component on the managed Slot that no key holds, as a user could add by hand.
    private static async Task<string> AddUnmanagedRendererAsync(FakeResoniteClient client)
    {
        var created = await client.AddComponentAsync(Rubble(client).Id, "Test.Renderer",
            new Dictionary<string, string> { ["Materials"] = $"[\"{MaterialIds(client, 1)[0]}\"]", ["Label"] = "west" });
        client.ResetWriteCounts();
        return created.Id;
    }

    private static void AssertUnmanagedRendererUnchanged(FakeResoniteClient client, string id)
    {
        var component = Assert.Single(Rubble(client).Components, component => component.Id == id);
        Assert.Equal("west", component.Members["Label"].Value!.GetValue<string>());
        Assert.Single(MaterialTargets(component));
    }

    // The saved index counts the Component another key holds, so leaving that Component out of matching must not shift the
    // index that finds a Component whose create response was lost onto an unmanaged Component of the same type.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedRecreateBindsItsReplacementNextToAnUnmanagedSameTypeComponent(bool unmanagedBeforeInterruption)
    {
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"south"}}""";
        var name = "shrink-resume-unmanaged-" + unmanagedBeforeInterruption;
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name, extraComponents: sibling);
        var siblingId = StateComponent(state, "sibling")["id"]!.GetValue<string>();
        var three = ShrinkDocument(name, 3, extraComponents: sibling);
        var unmanagedId = unmanagedBeforeInterruption ? await AddUnmanagedRendererAsync(client) : null;
        client.LoseNextComponentCreateResponse = true;
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        var replacementId = Assert.Single(RendererIds(Rubble(client)), id => id != oldId && id != siblingId && id != unmanagedId);
        unmanagedId ??= await AddUnmanagedRendererAsync(client);

        await service.ApplyAsync(three, new ApplyOptions(state));

        Assert.Equal(replacementId, Renderer(client).Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(Renderer(client)));
        Assert.Equal(replacementId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(3, RendererIds(Rubble(client)).Length);
        AssertUnmanagedRendererUnchanged(client, unmanagedId);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedCreateOfANewKeyBindsWhatItCreatedNextToAnUnmanagedSameTypeComponent(bool unmanagedBeforeInterruption)
    {
        const string extra = """,{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        var name = "new-key-resume-unmanaged-" + unmanagedBeforeInterruption;
        var (client, service, state, rendererId) = await ApplyFourMaterialsAsync(name);
        var withExtra = ShrinkDocument(name, 4, extraComponents: extra);
        var unmanagedId = unmanagedBeforeInterruption ? await AddUnmanagedRendererAsync(client) : null;
        client.LoseNextComponentCreateResponse = true;
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(withExtra, new ApplyOptions(state))));
        var createdId = Assert.Single(RendererIds(Rubble(client)), id => id != rendererId && id != unmanagedId);
        unmanagedId ??= await AddUnmanagedRendererAsync(client);

        await service.ApplyAsync(withExtra, new ApplyOptions(state));

        Assert.Equal(createdId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(rendererId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(3, RendererIds(Rubble(client)).Length);
        AssertUnmanagedRendererUnchanged(client, unmanagedId);
        client.ResetWriteCounts();
        await service.ApplyAsync(withExtra, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // Leaving out the Component another key holds never resolves an ambiguity: without an ID or an index that still points
    // at a candidate, an unmanaged Component is not adopted.
    [Fact]
    public async Task InterruptedCreateOfANewKeyDoesNotAdoptAnUnmanagedSameTypeComponent()
    {
        const string extra = """,{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        var (client, service, state, _) = await ApplyFourMaterialsAsync("new-key-unmanaged");
        var withExtra = ShrinkDocument("new-key-unmanaged", 4, extraComponents: extra);
        var unmanagedId = await AddUnmanagedRendererAsync(client);
        client.FailOnWrite = 1;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));
        client.FailOnWrite = null;
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(0, client.Writes);
        AssertUnmanagedRendererUnchanged(client, unmanagedId);
    }

    // A key whose create was interrupted may move before the re-run. The Component another key holds on the old Slot is not
    // the one to move, so the Component is created on the new Slot.
    [Fact]
    public async Task InterruptedCreateOfANewKeyThatMovesIsCreatedOnTheNewSlot()
    {
        const string extra = """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        var (client, service, state, rendererId) = await ApplyFourMaterialsAsync("new-key-move");
        client.FailOnWrite = 1;
        await Assert.ThrowsAsync<IOException>(() =>
            service.ApplyAsync(ShrinkDocument("new-key-move", 4, extraComponents: "," + extra), new ApplyOptions(state)));
        client.FailOnWrite = null;
        var materialComponents = string.Join(",", Enumerable.Range(1, 4).Select(i =>
            $$"""{"key":"m{{i}}","type":"Test.Material","fields":{"Tint":{{i}} } }"""));
        var moved = ReloadDocument("new-key-move", $$$"""
            {"schemaVersion":"1","ownership":{"key":"new-key-move"},"slot":{"key":"root","name":"Plaza","parent":"Root"},
             "components":[{{{materialComponents}}}],
             "children":[{"slot":{"key":"rubble","name":"Rubble"},
               "components":[{"key":"renderer","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"north"} }],
               "children":[{"slot":{"key":"debris","name":"Debris"},"components":[{{{extra}}}]}]}]}
            """);

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state));
        await service.ApplyAsync(moved, new ApplyOptions(state));

        Assert.Equal("create", Assert.Single(plan.Changes, change => change.Key == "extra").Action);
        Assert.Equal([rendererId], RendererIds(Rubble(client)));
        var added = Assert.Single(Rubble(client).Children.Single().Components, component => component.Type == "Test.Renderer");
        Assert.Equal(added.Id, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(rendererId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        client.ResetWriteCounts();
        await service.ApplyAsync(moved, new ApplyOptions(state));
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

    // The candidates after a reload include other keys' Components of the same type, so each carries its field values. Until
    // the replacement's ID is saved it may not exist, and clearing supersededId alone would bind the key to a sibling, so the
    // hint removes the key instead. Following the hint converges and keeps the sibling.
    [Theory]
    [InlineData("before-add")]
    [InlineData("lost-add-response")]
    [InlineData("before-remove")]
    public async Task InterruptedRecreateNextToASameTypeSiblingRecoversAfterWorldReloadByFollowingTheHint(string failure)
    {
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"south"}}""";
        var name = "shrink-reload-sibling-" + failure;
        var (client, service, state, _) = await ApplyFourMaterialsAsync(name, extraComponents: sibling);
        var three = ShrinkDocument(name, 3, extraComponents: sibling);
        if (failure == "lost-add-response") client.LoseNextComponentCreateResponse = true;
        else client.FailOnWrite = failure == "before-add" ? 1 : 2;
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        client.FailOnWrite = null;
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", error.Code);
        Assert.Equal(0, client.Writes);
        var candidates = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(error.Context))!["candidates"]!.AsArray();
        Assert.Equal(RendererIds(Rubble(client)), candidates.Select(candidate => candidate!["id"]!.GetValue<string>()));
        foreach (var candidate in candidates)
            Assert.Equal(Rubble(client).Components.Single(component => component.Id == candidate!["id"]!.GetValue<string>()).Members["Label"].Value!.GetValue<string>(),
                candidate!["fields"]!["Label"]!.GetValue<string>());
        var savedIdEmpty = failure != "before-remove";
        Assert.Contains(error.Suggestions, suggestion =>
            suggestion.Contains(savedIdEmpty ? "remove this key from the state file" : "remove supersededId from this key"));

        var north = Rubble(client).Components.Where(component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == "north").ToArray();
        var siblingId = Assert.Single(RendererIds(Rubble(client)), id => north.All(component => component.Id != id));
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        if (savedIdEmpty)
        {
            foreach (var component in north) await client.RemoveComponentAsync(component.Id);
            saved["components"]!.AsObject().Remove("renderer");
        }
        else
        {
            await client.RemoveComponentAsync(north.MaxBy(component => MaterialTargets(component).Length)!.Id);
            saved["components"]!["renderer"]!.AsObject().Remove("supersededId");
        }
        File.WriteAllText(state, saved.ToJsonString());
        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([siblingId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(4, MaterialTargets(Rubble(client).Components.Single(component => component.Id == siblingId)).Length);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
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
        RefillRenderers(client);

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

    // A resumed recreate binds the replacement an interrupted apply created, so it must be verified like a new one.
    [Fact]
    public async Task ResumedRecreateThatTheRuntimeRefilledStopsAndKeepsTheOriginal()
    {
        const string extra = """,{"key":"extra","type":"Test.Target","fields":{"Enabled":true}}""";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-refill-resume");
        var three = ShrinkDocument("shrink-refill-resume", 3, extraComponents: extra);
        RefillRenderers(client);
        // The second write creates the extra Component, after the replacement and before its verification.
        client.FailOnWrite = 2;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        var replacementId = StateComponent(state, "renderer")["id"]!.GetValue<string>();
        Assert.Equal([oldId, replacementId], RendererIds(Rubble(client)));

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal([oldId], RendererIds(Rubble(client)));
        var saved = StateComponent(state, "renderer");
        Assert.Equal(oldId, saved["id"]!.GetValue<string>());
        Assert.Null(saved["supersededId"]);
        Assert.Equal(Rubble(client).Components.FindIndex(component => component.Id == oldId), saved["componentIndex"]!.GetValue<int>());
    }

    // Undoing a recreate keeps the replaced Component in place, so its siblings keep their actual positions in state.
    [Fact]
    public async Task UndoneRecreateKeepsSiblingIndexesSoAWorldReloadStillBindsThem()
    {
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"south"}}""";
        var (client, service, state, _) = await ApplyFourMaterialsAsync("shrink-refill-sibling", extraComponents: sibling);
        RefillRenderers(client);

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(ShrinkDocument("shrink-refill-sibling", 3, extraComponents: sibling), new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        var components = Rubble(client).Components;
        foreach (var key in new[] { "renderer", "sibling" })
        {
            var saved = StateComponent(state, key);
            Assert.Equal(components.FindIndex(component => component.Id == saved["id"]!.GetValue<string>()),
                saved["componentIndex"]!.GetValue<int>());
        }
        client.AfterComponentAdded = null;
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        var four = ShrinkDocument("shrink-refill-sibling", 4, extraComponents: sibling);
        var plan = await service.PlanApplyAsync(four, new ApplyOptions(state));
        await service.ApplyAsync(four, new ApplyOptions(state));
        Assert.Empty(plan.Changes);
        Assert.Equal(0, client.Writes);
    }

    // A Component created in the same apply must not keep the ID of a replacement that the undo removes.
    [Fact]
    public async Task UndoneRecreateLeavesNoReferenceToTheRemovedReplacement()
    {
        const string source = """,{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-refill-source");
        RefillRenderers(client);

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(ShrinkDocument("shrink-refill-source", 3, extraComponents: source), new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        var created = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Source");
        Assert.NotEqual(error.Context["removedReplacementId"], created.Members["Target"].TargetId);
        client.AfterComponentAdded = null;
        await service.ApplyAsync(ShrinkDocument("shrink-refill-source", 4, extraComponents: source), new ApplyOptions(state));
        Assert.Equal(oldId, created.Members["Target"].TargetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PruningAKeyWhoseRecreateWasInterruptedAlsoRemovesTheReplacedComponent(bool interruptBetweenRemovals)
    {
        var name = "shrink-prune-" + interruptBetweenRemovals;
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name);
        await InterruptBeforeRemovingReplacedAsync(client, service, ShrinkDocument(name, 3), state);
        var replacementId = StateComponent(state, "renderer")["id"]!.GetValue<string>();
        var removed = RendererElsewhereDocument(name, moved: false);
        var prune = new ApplyOptions(state, Prune: true, ConfirmDeletes: true);

        var plan = await service.PlanApplyAsync(removed, prune);
        Assert.Equal(2, plan.Operations.Count(operation => operation.Action == "delete" && operation.Key == "renderer"));
        if (interruptBetweenRemovals)
        {
            // The replaced Component goes first, and its key keeps tracking the replacement until that is removed too.
            client.FailOnWrite = 2;
            await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(removed, prune));
            client.FailOnWrite = null;
            Assert.Equal([replacementId], RendererIds(Rubble(client)));
            Assert.Equal(replacementId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
            Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        }
        await service.ApplyAsync(removed, prune);

        Assert.DoesNotContain(oldId, RendererIds(Rubble(client)));
        Assert.Empty(RendererIds(Rubble(client)));
        Assert.Null(JsonNode.Parse(File.ReadAllText(state))!["components"]!["renderer"]);
    }

    [Fact]
    public async Task MovingAComponentWhoseRecreateWasInterruptedAlsoRemovesTheReplacedComponent()
    {
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-move");
        await InterruptBeforeRemovingReplacedAsync(client, service, ShrinkDocument("shrink-move", 3), state);
        var moved = RendererElsewhereDocument("shrink-move", moved: true);

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state));
        await service.ApplyAsync(moved, new ApplyOptions(state));

        var entry = Assert.Single(plan.Changes, change => change.Key == "renderer");
        Assert.Equal("relocate", entry.Action);
        Assert.Contains(oldId, entry.Reason);
        Assert.Empty(RendererIds(Rubble(client)));
        var debris = Rubble(client).Children.Single();
        var renderer = Assert.Single(debris.Components, component => component.Type == "Test.Renderer");
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // A reload renumbers both halves, so moving or removing the key cannot tell which one to keep either.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedRecreateStopsBeforeMutationAfterWorldReloadWhenTheKeyMovesOrIsRemoved(bool moved)
    {
        var name = "shrink-reload-elsewhere-" + moved;
        var (client, service, state, _) = await ApplyFourMaterialsAsync(name);
        await InterruptBeforeRemovingReplacedAsync(client, service, ShrinkDocument(name, 3), state);
        client.ReloadWorld("session-reloaded");
        var renderers = RendererIds(Rubble(client));
        Assert.Equal(2, renderers.Length);
        var document = RendererElsewhereDocument(name, moved);
        var options = new ApplyOptions(state, Prune: !moved, ConfirmDeletes: !moved);
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(document, options));
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, options));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", planError.Code);
        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", applyError.Code);
        var context = System.Text.Json.JsonSerializer.Serialize(applyError.Context);
        Assert.All(renderers, id => Assert.Contains(id, context));
        Assert.Equal(0, client.Writes);
        Assert.Equal(renderers, RendererIds(Rubble(client)));
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
    }
}
