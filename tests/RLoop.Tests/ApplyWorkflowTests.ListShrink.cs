using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    // ResoniteLink 0.13.1 cannot remove list elements: an update only replaces leading elements. A declared list that is
    // shorter than the runtime list can only converge by recreating the Component.
    private const string Identity = ""","identityFields":["Label"]""";

    private ApplyDocument ShrinkDocument(string name, int materials, string rendererExtra = "", string extraComponents = "",
        string leadingComponents = "")
    {
        var materialComponents = string.Join(",", Enumerable.Range(1, 4).Select(i =>
            $$"""{"key":"m{{i}}","type":"Test.Material","fields":{"Tint":{{i}} } }"""));
        var list = string.Join(",", Enumerable.Range(1, materials).Select(i => $"\"$ref:m{i}\""));
        return ReloadDocument(name, $$"""
            {"schemaVersion":"1","ownership":{"key":"{{name}}"},"slot":{"key":"root","name":"Plaza","parent":"Root"},
             "components":[{{materialComponents}}],
             "children":[{"slot":{"key":"rubble","name":"Rubble"},
               "components":[{{leadingComponents}}{"key":"renderer","type":"Test.Renderer","fields":{"Materials":[{{list}}],"Label":"north"}{{rendererExtra}} }{{extraComponents}}]}]}
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

    // A partial syncObject declaration writes only its declared children, so a reference in a child it leaves out would keep
    // pointing at the removed Component. Only a reference whose own child carries a declared selector is re-pointed, however
    // many other children of the member do.
    [Theory]
    [InlineData("undeclared-child")]
    [InlineData("declared-child")]
    [InlineData("no-reference")]
    public async Task ListShrinkChecksEachNestedReferenceAgainstItsOwnDeclaredChild(string variant)
    {
        var name = "shrink-nested-" + variant;
        var snapPositions = variant == "declared-child"
            ? """{"LocalSpace":"$slot:root","Target":"$ref:renderer"}""" : """{"LocalSpace":"$slot:root"}""";
        var consumerSpec = $$"""{"key":"consumer","type":"Test.Slider","fields":{"SnapPositions":{{snapPositions}} } }""";
        var four = RubbleDocument(name, [RendererSpec("renderer", "north", 4), consumerSpec]);
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        var oldId = Renderer(client).Id;
        var consumer = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Slider");
        var children = new Dictionary<string, MemberValue>
        {
            ["LocalSpace"] = new("reference", consumer.Id + ":SnapPositions.LocalSpace",
                TargetId: client.Root.Children.Single(slot => slot.Name == "Plaza").Id),
            ["Offset"] = new("field", consumer.Id + ":SnapPositions.Offset", "int", JsonValue.Create(1))
        };
        if (variant != "no-reference") children["Target"] = new("reference", consumer.Id + ":SnapPositions.Target", TargetId: oldId);
        consumer.Members["SnapPositions"] = new("syncObject", consumer.Id + ":SnapPositions", Members: children);
        client.ResetWriteCounts();
        var three = RubbleDocument(name, [RendererSpec("renderer", "north", 3), consumerSpec]);

        if (variant == "undeclared-child")
        {
            var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(three, new ApplyOptions(state)));
            var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
            Assert.Equal("APPLY_LIST_SHRINK_REFERENCED", planError.Code);
            Assert.Equal("APPLY_LIST_SHRINK_REFERENCED", applyError.Code);
            Assert.Contains(consumer.Id, System.Text.Json.JsonSerializer.Serialize(applyError.Context));
            Assert.Equal(0, client.Writes);
            Assert.Equal(oldId, Renderer(client).Id);
            Assert.Equal(oldId, consumer.Members["SnapPositions"].Members!["Target"].TargetId);
            return;
        }
        await service.ApplyAsync(three, new ApplyOptions(state));
        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        if (variant == "declared-child")
            Assert.Contains(renderer.Id, consumer.Members["SnapPositions"].Value!.ToJsonString());
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

    // A new key created next to a recreate is saved at its index in the layout the recreate leaves, so the Component the recreate
    // removes must not shift that index onto another key's Component: the key would be created again and the first one orphaned.
    // The recreate may already be under way or, when the new key is declared first, not yet recorded in state.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedCreateOfANewKeyNextToARecreateBindsWhatItCreated(bool declaredBeforeRecreate)
    {
        const string extra = """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        var name = "recreate-new-key-resume-" + declaredBeforeRecreate;
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name);
        var three = declaredBeforeRecreate
            ? ShrinkDocument(name, 3, leadingComponents: extra + ",")
            : ShrinkDocument(name, 3, extraComponents: "," + extra);
        client.AfterComponentAdded = component =>
        {
            if (component.Type == "Test.Renderer" && component.Members["Label"].Value!.GetValue<string>() == "east")
                client.LoseNextComponentCreateResponse = true;
        };
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        client.AfterComponentAdded = null;
        var createdId = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == "east").Id;

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal(declaredBeforeRecreate ? [createdId, renderer.Id] : [renderer.Id, createdId], RendererIds(Rubble(client)));
        Assert.Equal(createdId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
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

    // Without a saved ID a key whose create was interrupted matches by type and position alone. A Component matched that way
    // whose list is longer than declared may be one this document does not manage, so apply stops instead of recreating, and
    // so removing, it. Removing the key from state, as the hint says, creates the key's Component next to it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCreateOfANewKeyDoesNotRecreateAnUnmanagedComponentWithALongerList(bool reload)
    {
        const string extra = """,{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        var name = "new-key-unmanaged-longer-" + reload;
        var (client, service, state, _) = await ApplyFourMaterialsAsync(name);
        var withExtra = ShrinkDocument(name, 4, extraComponents: extra);
        client.FailOnWrite = 1;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));
        client.FailOnWrite = null;
        await client.AddComponentAsync(Rubble(client).Id, "Test.Renderer", new Dictionary<string, string>
        {
            ["Materials"] = "[" + string.Join(",", MaterialIds(client, 4).Select(id => $"\"{id}\"")) + "]", ["Label"] = "west"
        });
        if (reload) client.ReloadWorld("session-reloaded");
        var unmanagedId = RendererIds(Rubble(client))[1];
        client.ResetWriteCounts();

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(withExtra, new ApplyOptions(state)));
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", planError.Code);
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(0, client.Writes);
        var context = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(error.Context))!;
        Assert.Equal([unmanagedId], context["candidateIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal("extra", context["componentKey"]!.GetValue<string>());
        Assert.Contains(error.Suggestions, suggestion => suggestion.Contains("remove this key from the state file"));
        AssertUnmanagedUnchanged();

        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        saved["components"]!.AsObject().Remove("extra");
        File.WriteAllText(state, saved.ToJsonString());
        await service.ApplyAsync(withExtra, new ApplyOptions(state));

        var created = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == "east");
        Assert.Equal([MaterialIds(client, 4)[3]], MaterialTargets(created));
        Assert.Equal(created.Id, StateComponent(state, "extra")["id"]!.GetValue<string>());
        AssertUnmanagedUnchanged();
        client.ResetWriteCounts();
        await service.ApplyAsync(withExtra, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);

        void AssertUnmanagedUnchanged()
        {
            var component = Assert.Single(Rubble(client).Components, component => component.Id == unmanagedId);
            Assert.Equal("west", component.Members["Label"].Value!.GetValue<string>());
            Assert.Equal(MaterialIds(client, 4), MaterialTargets(component));
        }
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
    // hint removes the key instead. Following the hint converges and keeps the siblings, however many there are after the
    // recreated key: the layout the hint leaves is the one their saved indexes describe.
    [Theory]
    [InlineData("before-add", 1)]
    [InlineData("lost-add-response", 1)]
    [InlineData("before-remove", 1)]
    [InlineData("before-add", 2)]
    [InlineData("lost-add-response", 2)]
    public async Task InterruptedRecreateNextToSameTypeSiblingsRecoversAfterWorldReloadByFollowingTheHint(string failure, int siblingCount)
    {
        var siblingKeys = new[] { ("sibling", "south"), ("sibling2", "east") }.Take(siblingCount).ToArray();
        var siblings = string.Concat(siblingKeys.Select(sibling =>
            $$$""",{"key":"{{{sibling.Item1}}}","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"{{{sibling.Item2}}}"}}"""));
        var name = $"shrink-reload-siblings-{failure}-{siblingCount}";
        var (client, service, state, _) = await ApplyFourMaterialsAsync(name, extraComponents: siblings);
        var three = ShrinkDocument(name, 3, extraComponents: siblings);
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
        var siblingIds = RendererIds(Rubble(client)).Where(id => north.All(component => component.Id != id)).ToArray();
        Assert.Equal(siblingCount, siblingIds.Length);
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
        Assert.Equal([.. siblingIds, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        for (var i = 0; i < siblingCount; i++)
        {
            Assert.Equal(siblingIds[i], StateComponent(state, siblingKeys[i].Item1)["id"]!.GetValue<string>());
            Assert.Equal(4, MaterialTargets(Rubble(client).Components.Single(component => component.Id == siblingIds[i])).Length);
        }
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // A key processed before the recreate can stop the apply before the recreate saves supersededId. Until then the siblings
    // keep their indexes in the layout the Slot still has, so after a world reload they bind where they are and the re-run
    // converges instead of stopping on an ownership conflict. A sibling declared first can still sit after the recreated key.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task ApplyStoppedBeforeARecreateIsSavedConvergesAfterWorldReload(bool siblingDeclaredFirst, int siblingCount)
    {
        var siblingKeys = new[] { ("sibling", "south"), ("sibling2", "east") }.Take(siblingCount).ToArray();
        var siblings = siblingKeys.Select(sibling =>
            $$$"""{"key":"{{{sibling.Item1}}}","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"{{{sibling.Item2}}}"}}""").ToArray();
        var leading = string.Concat(siblings.Select(sibling => sibling + ","));
        var trailing = string.Concat(siblings.Select(sibling => "," + sibling));
        const string marker = """{"key":"marker","type":"Test.Material","fields":{"Tint":9}},""";
        var name = $"shrink-reload-before-recreate-{siblingDeclaredFirst}-{siblingCount}";
        FakeResoniteClient client;
        WorldService service;
        string state;
        ApplyDocument three;
        if (siblingDeclaredFirst)
        {
            // The sibling is added by a later apply, so it sits after the renderer on the Slot.
            (client, service, state, _) = await ApplyFourMaterialsAsync(name);
            await service.ApplyAsync(ShrinkDocument(name, 4, leadingComponents: leading), new ApplyOptions(state));
            three = ShrinkDocument(name, 3, leadingComponents: leading + marker);
        }
        else
        {
            (client, service, state, _) = await ApplyFourMaterialsAsync(name, extraComponents: trailing);
            three = ShrinkDocument(name, 3, extraComponents: trailing, leadingComponents: marker);
        }
        client.ResetWriteCounts();
        client.FailOnWrite = 1;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ReloadWorld("session-reloaded");
        var siblingIds = siblingKeys.Select(sibling => Assert.Single(Rubble(client).Components, component =>
            component.Type == "Test.Renderer" && component.Members["Label"].Value!.GetValue<string>() == sibling.Item2).Id).ToArray();
        client.ResetWriteCounts();

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([.. siblingIds, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        for (var i = 0; i < siblingCount; i++)
        {
            Assert.Equal(siblingIds[i], StateComponent(state, siblingKeys[i].Item1)["id"]!.GetValue<string>());
            Assert.Equal(4, MaterialTargets(Rubble(client).Components.Single(component => component.Id == siblingIds[i])).Length);
        }
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    private static FakeResoniteClient.FakeComponent LabeledRenderer(FakeResoniteClient client, string label) =>
        Assert.Single(Rubble(client).Components, component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == label);

    // A new key created before the Slot's recreate saves supersededId sits after the Component the recreate replaces.
    // Until that checkpoint it is saved at its index in the layout the Slot has, so after a world reload it binds what it
    // created and the recreate converges instead of stopping on an ownership conflict or an ambiguity.
    [Theory]
    [InlineData("write-failure")]
    [InlineData("cancel")]
    [InlineData("lost-add-response")]
    public async Task NewKeyCreatedBeforeARecreateConvergesAfterWorldReload(string failure)
    {
        const string extra = """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}},""";
        const string marker = """{"key":"marker","type":"Test.Material","fields":{"Tint":9}},""";
        var name = "shrink-reload-new-key-" + failure;
        var (client, service, state, _) = await ApplyFourMaterialsAsync(name);
        var three = ShrinkDocument(name, 3, leadingComponents: extra + (failure == "write-failure" ? marker : ""));
        using var cancellation = new CancellationTokenSource();
        if (failure == "write-failure") client.FailOnWrite = 2;
        else if (failure == "cancel") { client.Cancellation = cancellation; client.CancelAfterWrites = 1; }
        else client.LoseNextComponentCreateResponse = true;

        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state), cancellation.Token)));
        client.FailOnWrite = null;
        client.CancelAfterWrites = null;
        client.Cancellation = null;
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        var createdId = LabeledRenderer(client, "east").Id;
        Assert.Equal(Rubble(client).Components.FindIndex(component => component.Id == createdId),
            StateComponent(state, "extra")["componentIndex"]!.GetValue<int>());
        client.ReloadWorld("session-reloaded");
        var extraId = LabeledRenderer(client, "east").Id;
        client.ResetWriteCounts();

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([extraId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(extraId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // Resuming a new key's lost create in the same session matches it in the layout its saved index describes. With a
    // same-type sibling the index, not a single remaining candidate, has to point at the Component it created.
    [Fact]
    public async Task ResumedCreateOfANewKeyBeforeARecreateNextToASameTypeSiblingBindsWhatItCreated()
    {
        const string extra = """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}},""";
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"south"}}""";
        const string name = "shrink-resume-new-key-sibling";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name, extraComponents: sibling);
        var siblingId = StateComponent(state, "sibling")["id"]!.GetValue<string>();
        var three = ShrinkDocument(name, 3, extraComponents: sibling, leadingComponents: extra);
        client.LoseNextComponentCreateResponse = true;

        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        var createdId = LabeledRenderer(client, "east").Id;
        Assert.Equal(Rubble(client).Components.FindIndex(component => component.Id == createdId),
            StateComponent(state, "extra")["componentIndex"]!.GetValue<int>());

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([siblingId, createdId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(createdId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // Once the recreate saves the Slot's first supersededId, a new key created before it is saved at its final index in
    // the same checkpoint, so following the reload hint leaves the layout that index describes.
    [Fact]
    public async Task NewKeyCreatedBeforeAnInterruptedRecreateIsSavedAtItsFinalIndex()
    {
        const string extra = """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}},""";
        const string name = "shrink-new-key-final-index";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name);
        var three = ShrinkDocument(name, 3, leadingComponents: extra);
        client.FailOnWrite = 2;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal(oldId, StateComponent(state, "renderer")["supersededId"]!.GetValue<string>());
        Assert.Equal(0, StateComponent(state, "extra")["componentIndex"]!.GetValue<int>());
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", error.Code);
        Assert.Equal(0, client.Writes);

        // The replacement's ID was not saved: remove every candidate with this key's fields, then the key.
        await client.RemoveComponentAsync(Renderer(client).Id);
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        saved["components"]!.AsObject().Remove("renderer");
        File.WriteAllText(state, saved.ToJsonString());
        var extraId = LabeledRenderer(client, "east").Id;
        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([extraId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(extraId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // A resumed recreate on a Slot that already saved supersededId keeps every key there at its final index: an apply that
    // stops again at a sibling's checkpoint must not save the sibling back at its index in the layout the Slot still has.
    // The sibling is declared first but added by a later apply, so it sits after the renderer on the Slot.
    [Theory]
    [InlineData("before-add")]
    [InlineData("before-remove")]
    public async Task ResumedRecreateStoppedAtASiblingKeepsItsFinalIndexAcrossWorldReload(string failure)
    {
        const string sibling = """{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"south"}},""";
        var name = "shrink-resume-stopped-at-sibling-" + failure;
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name);
        await service.ApplyAsync(ShrinkDocument(name, 4, leadingComponents: sibling), new ApplyOptions(state));
        var three = ShrinkDocument(name, 3, leadingComponents: sibling);
        client.ResetWriteCounts();
        client.FailOnWrite = failure == "before-add" ? 1 : 2;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal(oldId, StateComponent(state, "renderer")["supersededId"]!.GetValue<string>());
        Assert.Equal(0, StateComponent(state, "sibling")["componentIndex"]!.GetValue<int>());

        using var cancellation = new CancellationTokenSource();
        var cancelledAtSibling = false;
        var stopAtSibling = new ApplyOptions(state, Progress: progress =>
        {
            if (progress.Stage == "components" && progress.Path?.EndsWith("/@sibling", StringComparison.Ordinal) == true)
            {
                cancelledAtSibling = true;
                cancellation.Cancel();
            }
        });
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, stopAtSibling, cancellation.Token)));
        Assert.True(cancelledAtSibling);
        Assert.Equal(0, StateComponent(state, "sibling")["componentIndex"]!.GetValue<int>());
        Assert.Equal(oldId, StateComponent(state, "renderer")["supersededId"]!.GetValue<string>());

        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", error.Code);
        Assert.Equal(0, client.Writes);

        var siblingId = LabeledRenderer(client, "south").Id;
        var north = Rubble(client).Components.Where(component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == "north").ToArray();
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        if (failure == "before-add")
        {
            // The replacement's ID was not saved: remove every candidate with this key's fields, then the key.
            foreach (var component in north) await client.RemoveComponentAsync(component.Id);
            saved["components"]!.AsObject().Remove("renderer");
        }
        else
        {
            // Remove only the replaced Component, whose list is longer than declared, then supersededId.
            await client.RemoveComponentAsync(north.MaxBy(component => MaterialTargets(component).Length)!.Id);
            saved["components"]!["renderer"]!.AsObject().Remove("supersededId");
        }
        File.WriteAllText(state, saved.ToJsonString());
        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([siblingId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(4, MaterialTargets(LabeledRenderer(client, "south")).Length);
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // A new key declared before a recreate is saved at its index in the layout the Slot has, past its last Component, so when
    // its create fails before it runs the index tells none of the same-type Components apart. In the same session other keys
    // hold all of them, and a Component another key holds is never this key's, so the re-run creates the key instead of
    // stopping on an ambiguity.
    [Fact]
    public async Task InterruptedCreateOfANewKeyBeforeARecreateNextToASameTypeSiblingResumes()
    {
        const string extra = """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}},""";
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"south"}}""";
        const string name = "shrink-resume-failed-new-key-sibling";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name, extraComponents: sibling);
        var siblingId = StateComponent(state, "sibling")["id"]!.GetValue<string>();
        var three = ShrinkDocument(name, 3, extraComponents: sibling, leadingComponents: extra);
        client.FailOnWrite = 1;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal("", StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(Rubble(client).Components.Count, StateComponent(state, "extra")["componentIndex"]!.GetValue<int>());
        Assert.Equal([oldId, siblingId], RendererIds(Rubble(client)));

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        var extraId = LabeledRenderer(client, "east").Id;
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([siblingId, extraId, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(extraId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(4, MaterialTargets(LabeledRenderer(client, "south")).Length);
        Assert.Equal([MaterialIds(client, 4)[3]], MaterialTargets(LabeledRenderer(client, "east")));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // The same holds for a recreate whose replacement's create fails before it runs next to two same-type siblings: without
    // the replaced Component only the siblings are left to match, and the saved final index is past both.
    [Fact]
    public async Task InterruptedRecreateNextToTwoSameTypeSiblingsResumes()
    {
        var siblingKeys = new[] { ("sibling", "south"), ("sibling2", "west") };
        var siblings = string.Concat(siblingKeys.Select(sibling =>
            $$$""",{"key":"{{{sibling.Item1}}}","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"{{{sibling.Item2}}}"}}"""));
        const string name = "shrink-resume-two-siblings";
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name, extraComponents: siblings);
        var siblingIds = siblingKeys.Select(sibling => StateComponent(state, sibling.Item1)["id"]!.GetValue<string>()).ToArray();
        var three = ShrinkDocument(name, 3, extraComponents: siblings);
        client.FailOnWrite = 1;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal("", StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(oldId, StateComponent(state, "renderer")["supersededId"]!.GetValue<string>());
        Assert.Equal([oldId, .. siblingIds], RendererIds(Rubble(client)));

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.NotEqual(oldId, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([.. siblingIds, renderer.Id], RendererIds(Rubble(client)));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(StateComponent(state, "renderer")["supersededId"]);
        for (var i = 0; i < siblingKeys.Length; i++)
        {
            Assert.Equal(siblingIds[i], StateComponent(state, siblingKeys[i].Item1)["id"]!.GetValue<string>());
            Assert.Equal(4, MaterialTargets(LabeledRenderer(client, siblingKeys[i].Item2)).Length);
        }
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // And for a new key next to two managed Components of its type on a Slot without a recreate.
    [Fact]
    public async Task InterruptedCreateOfANewKeyNextToTwoManagedSameTypeComponentsResumes()
    {
        const string sibling = """,{"key":"sibling","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3","$ref:m4"],"Label":"south"}}""";
        const string extra = """,{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"],"Label":"east"}}""";
        const string name = "new-key-resume-two-managed";
        var (client, service, state, rendererId) = await ApplyFourMaterialsAsync(name, extraComponents: sibling);
        var siblingId = StateComponent(state, "sibling")["id"]!.GetValue<string>();
        var withExtra = ShrinkDocument(name, 4, extraComponents: sibling + extra);
        client.FailOnWrite = 1;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal("", StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal([rendererId, siblingId], RendererIds(Rubble(client)));

        await service.ApplyAsync(withExtra, new ApplyOptions(state));

        var extraId = LabeledRenderer(client, "east").Id;
        Assert.Equal([rendererId, siblingId, extraId], RendererIds(Rubble(client)));
        Assert.Equal(rendererId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(extraId, StateComponent(state, "extra")["id"]!.GetValue<string>());
        Assert.Equal(MaterialIds(client, 4), MaterialTargets(Renderer(client)));
        Assert.Equal(4, MaterialTargets(LabeledRenderer(client, "south")).Length);
        Assert.Equal([MaterialIds(client, 4)[3]], MaterialTargets(LabeledRenderer(client, "east")));
        client.ResetWriteCounts();
        await service.ApplyAsync(withExtra, new ApplyOptions(state));
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

    // Without its saved ID a resumed recreate matches its replacement by type and position, and that can be a Component this
    // document does not manage, such as a copy the user made. Undoing the recreate never removes it.
    [Theory]
    [InlineData("before-add")]
    [InlineData("replacement-removed")]
    public async Task UndoneRecreateKeepsAReplacementMatchedWithoutItsSavedId(string failure)
    {
        var name = "shrink-undo-unsaved-" + failure;
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync(name);
        var three = ShrinkDocument(name, 3);
        client.FailOnWrite = failure == "before-add" ? 1 : 2;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailOnWrite = null;
        if (failure == "replacement-removed")
            await client.RemoveComponentAsync(StateComponent(state, "renderer")["id"]!.GetValue<string>());
        var copy = await client.AddComponentAsync(Rubble(client).Id, "Test.Renderer", new Dictionary<string, string>
        {
            ["Materials"] = "[" + string.Join(",", MaterialIds(client, 4).Select(id => $"\"{id}\"")) + "]", ["Label"] = "west"
        });
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal(0, client.Writes);
        Assert.Equal(copy.Id, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(error.Context))!["keptComponentId"]!.GetValue<string>());
        Assert.Equal([oldId, copy.Id], RendererIds(Rubble(client)));
        AssertCopyUnchanged();
        var saved = StateComponent(state, "renderer");
        Assert.Equal(oldId, saved["id"]!.GetValue<string>());
        Assert.Null(saved["supersededId"]);
        Assert.Equal(0, saved["componentIndex"]!.GetValue<int>());

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal([copy.Id, renderer.Id], RendererIds(Rubble(client)));
        AssertCopyUnchanged();
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);

        void AssertCopyUnchanged()
        {
            var component = Assert.Single(Rubble(client).Components, component => component.Id == copy.Id);
            Assert.Equal("west", component.Members["Label"].Value!.GetValue<string>());
            Assert.Equal(MaterialIds(client, 4), MaterialTargets(component));
        }
    }

    // A replacement whose create response was lost is matched the same way, so a refilled one is kept too, and reported.
    [Fact]
    public async Task ResumedRecreateKeepsARefilledReplacementWhoseCreateResponseWasLost()
    {
        var (client, service, state, oldId) = await ApplyFourMaterialsAsync("shrink-refill-lost");
        var three = ShrinkDocument("shrink-refill-lost", 3);
        RefillRenderers(client);
        client.LoseNextComponentCreateResponse = true;
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        var replacementId = Assert.Single(RendererIds(Rubble(client)), id => id != oldId);
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal(0, client.Writes);
        Assert.Equal(replacementId, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(error.Context))!["keptComponentId"]!.GetValue<string>());
        Assert.Equal([oldId, replacementId], RendererIds(Rubble(client)));
        var saved = StateComponent(state, "renderer");
        Assert.Equal(oldId, saved["id"]!.GetValue<string>());
        Assert.Null(saved["supersededId"]);
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

    private static string TargetingRendererSpec(string key, string label, int materials, string target) =>
        $$$"""{"key":"{{{key}}}","type":"Test.Renderer","fields":{"Materials":[{{{string.Join(",", Enumerable.Range(1, materials).Select(i => $"\"$ref:m{i}\""))}}}],"Label":"{{{label}}}","Target":"{{{target}}}"}}""";

    // Rubble's renderer, whose Target references the Renderer "second" after it on the Slot, applied with four materials each.
    private async Task<(FakeResoniteClient Client, WorldService Service, string State)> ApplyRendererTargetingSecondAsync(string name)
    {
        var four = RubbleDocument(name, [TargetingRendererSpec("renderer", "north", 4, "$ref:second"), RendererSpec("second", "south", 4)]);
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        client.ResetWriteCounts();
        return (client, service, state);
    }

    // A replacement is not referable until every replacement is verified. Otherwise the renderer's replacement, verified first,
    // would keep the ID of the second's replacement after the runtime refills that one and the undo removes it.
    [Fact]
    public async Task UndoneRecreateLeavesNoOtherReplacementReferencingTheRemovedOne()
    {
        const string name = "shrink-refill-cross";
        var (client, service, state) = await ApplyRendererTargetingSecondAsync(name);
        var oldId = Renderer(client).Id;
        var secondId = LabeledRenderer(client, "south").Id;
        client.AfterComponentAdded = component =>
        {
            if (component.Type != "Test.Renderer" || component.Members["Label"].Value?.ToJsonString() != "\"south\"") return;
            var materials = component.Members["Materials"];
            component.Members["Materials"] = materials with
            {
                Elements = [.. materials.Elements!, new MemberValue("reference", component.Id + ":Materials[3]", TargetId: "null")]
            };
        };

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(RubbleDocument(name,
            [TargetingRendererSpec("renderer", "north", 3, "$ref:second"), RendererSpec("second", "south", 3)]), new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal("second", error.Context["componentKey"]);
        var replacement = Assert.Single(Rubble(client).Components, component => component.Type == "Test.Renderer" &&
            component.Id != oldId && component.Members["Label"].Value!.GetValue<string>() == "north");
        Assert.NotEqual(error.Context["removedReplacementId"], replacement.Members["Target"].TargetId);
        client.AfterComponentAdded = null;
        var kept = RubbleDocument(name, [TargetingRendererSpec("renderer", "north", 3, "$ref:second"), RendererSpec("second", "south", 4)]);
        await service.ApplyAsync(kept, new ApplyOptions(state));
        Assert.Equal([secondId, replacement.Id], RendererIds(Rubble(client)));
        Assert.Equal(secondId, replacement.Members["Target"].TargetId);
        client.ResetWriteCounts();
        await service.ApplyAsync(kept, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // Once every replacement is verified, a replacement's reference to another one is written with the fields.
    [Fact]
    public async Task RecreateReferencingAnotherRecreateTargetsItsReplacement()
    {
        const string name = "shrink-cross";
        var (client, service, state) = await ApplyRendererTargetingSecondAsync(name);
        var three = RubbleDocument(name, [TargetingRendererSpec("renderer", "north", 3, "$ref:second"), RendererSpec("second", "south", 3)]);

        await service.ApplyAsync(three, new ApplyOptions(state));

        var renderer = Renderer(client);
        var second = LabeledRenderer(client, "south");
        Assert.Equal([renderer.Id, second.Id], RendererIds(Rubble(client)));
        Assert.Equal(second.Id, renderer.Members["Target"].TargetId);
        Assert.Equal(3, MaterialTargets(second).Length);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // A replacement that references itself or another replacement is created without its fields, so its create checkpoint
    // must not save those references as evidence it does not have yet. If the create response is lost, the re-run then binds
    // the Component it created instead of creating another one and leaving the first unmanaged.
    [Theory]
    [InlineData("self")]
    [InlineData("mutual")]
    public async Task ResumedRecreateCreatedWithoutItsReferencesBindsWhatItCreated(string references)
    {
        var name = "shrink-lost-references-" + references;
        string[] Components(int materials) => references == "self"
            ? [TargetingRendererSpec("renderer", "north", materials, "$ref:renderer")]
            : [TargetingRendererSpec("renderer", "north", materials, "$ref:second"), TargetingRendererSpec("second", "south", materials, "$ref:renderer")];
        var four = RubbleDocument(name, Components(4));
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        var before = RendererIds(Rubble(client));
        var three = RubbleDocument(name, Components(3));
        client.LoseNextComponentCreateResponse = true;
        Assert.NotNull(await Record.ExceptionAsync(() => service.ApplyAsync(three, new ApplyOptions(state))));
        var created = Assert.Single(RendererIds(Rubble(client)), id => !before.Contains(id));

        await service.ApplyAsync(three, new ApplyOptions(state));

        var tracked = JsonNode.Parse(File.ReadAllText(state))!["components"]!.AsObject()
            .Select(pair => pair.Value!["id"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(Components(3).Length, RendererIds(Rubble(client)).Length);
        Assert.All(RendererIds(Rubble(client)), id => Assert.Contains(id, tracked));
        var renderer = Renderer(client);
        Assert.Equal(created, renderer.Id);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal(references == "self" ? renderer.Id : LabeledRenderer(client, "south").Id, renderer.Members["Target"].TargetId);
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
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

    // Rubble with the given Components and, when any are given, a Debris child Slot holding debrisComponents.
    private ApplyDocument RubbleDocument(string name, IEnumerable<string> rubbleComponents, IEnumerable<string>? debrisComponents = null)
    {
        var materialComponents = string.Join(",", Enumerable.Range(1, 4).Select(i =>
            $$"""{"key":"m{{i}}","type":"Test.Material","fields":{"Tint":{{i}} } }"""));
        var debris = debrisComponents?.ToArray() ?? [];
        var children = debris.Length == 0 ? "" :
            $$""","children":[{"slot":{"key":"debris","name":"Debris"},"components":[{{string.Join(",", debris)}}]}]""";
        return ReloadDocument(name, $$"""
            {"schemaVersion":"1","ownership":{"key":"{{name}}"},"slot":{"key":"root","name":"Plaza","parent":"Root"},
             "components":[{{materialComponents}}],
             "children":[{"slot":{"key":"rubble","name":"Rubble"},"components":[{{string.Join(",", rubbleComponents)}}]{{children}} }]}
            """);
    }

    private static string RendererSpec(string key, string label, int materials) =>
        $$$"""{"key":"{{{key}}}","type":"Test.Renderer","fields":{"Materials":[{{{string.Join(",", Enumerable.Range(1, materials).Select(i => $"\"$ref:m{i}\""))}}}],"Label":"{{{label}}}"}}""";

    // A world reload binds keys by their saved indexes, so each must be the key's actual index on Rubble.
    private static void AssertSavedIndexesMatchRubble(FakeResoniteClient client, string state, params string[] keys)
    {
        foreach (var key in keys)
        {
            var id = StateComponent(state, key)["id"]!.GetValue<string>();
            Assert.Equal(Rubble(client).Components.FindIndex(component => component.Id == id),
                StateComponent(state, key)["componentIndex"]!.GetValue<int>());
        }
    }

    // A move or a prune in the same apply takes another Component off the Slot a recreate is on. The keys that stay are saved
    // at their indexes without it, wherever it sat, so a world reload binds them where they are instead of stopping on an
    // ambiguity that no re-run can clear.
    [Theory]
    [InlineData("move", false)]
    [InlineData("move", true)]
    [InlineData("prune", false)]
    [InlineData("prune", true)]
    public async Task ComponentRemovedFromARecreatingSlotLeavesIndexesAWorldReloadBinds(string removal, bool removedLast)
    {
        var name = $"shrink-remove-{removal}-{removedLast}";
        var sibling = RendererSpec("sibling", "south", 4);
        var doomed = RendererSpec("doomed", "west", 1);
        string[] RubbleComponents(int materials, bool withDoomed)
        {
            var renderer = RendererSpec("renderer", "north", materials);
            var components = removedLast ? new List<string> { renderer, sibling, doomed } : [renderer, doomed, sibling];
            if (!withDoomed) components.Remove(doomed);
            return [.. components];
        }
        var four = RubbleDocument(name, RubbleComponents(4, withDoomed: true));
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        var three = removal == "move"
            ? RubbleDocument(name, RubbleComponents(3, withDoomed: false), [doomed])
            : RubbleDocument(name, RubbleComponents(3, withDoomed: false));
        var prune = removal == "prune";

        await service.ApplyAsync(three, new ApplyOptions(state, Prune: prune, ConfirmDeletes: prune));

        AssertSavedIndexesMatchRubble(client, state, "renderer", "sibling");
        client.ReloadWorld("session-reloaded");
        var siblingId = LabeledRenderer(client, "south").Id;
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        var renderer = Renderer(client);
        Assert.Equal(MaterialIds(client, 3), MaterialTargets(renderer));
        Assert.Equal(renderer.Id, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Equal(siblingId, StateComponent(state, "sibling")["id"]!.GetValue<string>());
    }

    // Moving a key whose recreate was interrupted takes both the replacement and the replaced Component off its old Slot, so
    // the same-type siblings left there are saved at their indexes without either and a world reload binds them where they are.
    [Fact]
    public async Task MovingAKeyWhoseRecreateWasInterruptedLeavesSiblingIndexesAWorldReloadBinds()
    {
        const string name = "shrink-move-siblings";
        string[] siblings = [RendererSpec("sibling", "south", 4), RendererSpec("sibling2", "east", 4)];
        var four = RubbleDocument(name, [RendererSpec("renderer", "north", 4), .. siblings]);
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        client.ResetWriteCounts();
        await InterruptBeforeRemovingReplacedAsync(client, service, RubbleDocument(name, [RendererSpec("renderer", "north", 3), .. siblings]), state);
        var moved = RubbleDocument(name, siblings, [RendererSpec("renderer", "north", 3)]);

        await service.ApplyAsync(moved, new ApplyOptions(state));

        Assert.Equal(2, RendererIds(Rubble(client)).Length);
        AssertSavedIndexesMatchRubble(client, state, "sibling", "sibling2");
        client.ReloadWorld("session-reloaded");
        string[] siblingIds = [LabeledRenderer(client, "south").Id, LabeledRenderer(client, "east").Id];
        client.ResetWriteCounts();
        await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        Assert.Equal(siblingIds, RendererIds(Rubble(client)));
        Assert.Equal(siblingIds[0], StateComponent(state, "sibling")["id"]!.GetValue<string>());
        Assert.Equal(siblingIds[1], StateComponent(state, "sibling2")["id"]!.GetValue<string>());
    }

    // Rubble's renderer, and after it the key "old", which a later document drops without --prune.
    private async Task<(FakeResoniteClient Client, WorldService Service, string State)> DropOldAfterRendererAsync(string name, bool dropNow)
    {
        var four = RubbleDocument(name, [RendererSpec("renderer", "north", 4), RendererSpec("old", "south", 4)]);
        var client = new FakeResoniteClient(four);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(four, new ApplyOptions(state));
        if (dropNow) await service.ApplyAsync(RubbleDocument(name, [RendererSpec("renderer", "north", 4)]), new ApplyOptions(state));
        client.ResetWriteCounts();
        return (client, service, state);
    }

    // The undeclared key "old" is found by its label: an apply after a world reload does not refresh its saved ID.
    private static void AssertUndeclaredIndexMatchesRubble(FakeResoniteClient client, string state) =>
        Assert.Equal(Rubble(client).Components.IndexOf(LabeledRenderer(client, "south")),
            StateComponent(state, "old")["componentIndex"]!.GetValue<int>());

    // A key dropped from the document without --prune keeps its Component and state entry, and after a world reload it is
    // bound by its saved index. A recreate appends its replacement where the Components after the replaced one move up, so
    // such a key is saved at its index in the layout apply leaves, as the declared keys are, whether this apply or an earlier
    // one dropped it and whether the world was reloaded before the recreate. Otherwise it binds the replacement, and every plan
    // and apply stops on an ownership conflict.
    [Theory]
    [InlineData("same-apply")]
    [InlineData("earlier-apply")]
    [InlineData("earlier-apply-then-reload")]
    public async Task UndeclaredKeyOnARecreatingSlotKeepsAnIndexAWorldReloadBinds(string dropped)
    {
        var name = "shrink-undeclared-" + dropped;
        var (client, service, state) = await DropOldAfterRendererAsync(name, dropNow: dropped != "same-apply");
        if (dropped == "earlier-apply-then-reload") client.ReloadWorld("session-before-recreate");
        var three = RubbleDocument(name, [RendererSpec("renderer", "north", 3)]);

        await service.ApplyAsync(three, new ApplyOptions(state));

        AssertUndeclaredIndexMatchesRubble(client, state);
        client.ReloadWorld("session-reloaded");
        var rendererId = Renderer(client).Id;
        var plan = await service.PlanApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(["delete:old"], plan.Changes.Select(change => change.Action + ":" + change.Key));
        await service.ApplyAsync(three, new ApplyOptions(state, Prune: true, ConfirmDeletes: true));
        Assert.Equal([rendererId], RendererIds(Rubble(client)));
        Assert.Equal(rendererId, StateComponent(state, "renderer")["id"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(state))!["components"]!["old"]);
    }

    // The checkpoint that saves supersededId saves the undeclared key at its index in the layout the recreate leaves, as it does
    // the declared keys, so after a world reload the hint's recovery (remove the replaced Component) leaves the layout it describes.
    [Fact]
    public async Task InterruptedRecreateSavesAnUndeclaredKeyAtTheIndexTheReloadHintLeaves()
    {
        const string name = "shrink-undeclared-hint";
        var (client, service, state) = await DropOldAfterRendererAsync(name, dropNow: true);
        var three = RubbleDocument(name, [RendererSpec("renderer", "north", 3)]);
        await InterruptBeforeRemovingReplacedAsync(client, service, three, state);
        client.ReloadWorld("session-reloaded");
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        Assert.Equal("APPLY_RECREATE_INTERRUPTED_ACROSS_SESSION", error.Code);

        var replaced = Rubble(client).Components.Where(component => component.Type == "Test.Renderer" &&
            component.Members["Label"].Value!.GetValue<string>() == "north").MaxBy(component => MaterialTargets(component).Length)!;
        await client.RemoveComponentAsync(replaced.Id);
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        saved["components"]!["renderer"]!.AsObject().Remove("supersededId");
        File.WriteAllText(state, saved.ToJsonString());

        AssertUndeclaredIndexMatchesRubble(client, state);
        var plan = await service.PlanApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(["delete:old"], plan.Changes.Select(change => change.Action + ":" + change.Key));
        client.ResetWriteCounts();
        await service.ApplyAsync(three, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // Undoing a recreate keeps the replaced Component where it is, so the undeclared key goes back to its index in that layout.
    [Fact]
    public async Task UndoneRecreateKeepsAnUndeclaredKeyIndexAWorldReloadBinds()
    {
        const string name = "shrink-undeclared-undo";
        var (client, service, state) = await DropOldAfterRendererAsync(name, dropNow: true);
        RefillRenderers(client);

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(RubbleDocument(name, [RendererSpec("renderer", "north", 3)]), new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        AssertUndeclaredIndexMatchesRubble(client, state);
        client.AfterComponentAdded = null;
        client.ReloadWorld("session-reloaded");
        var plan = await service.PlanApplyAsync(RubbleDocument(name, [RendererSpec("renderer", "north", 4)]), new ApplyOptions(state));
        Assert.Equal(["delete:old"], plan.Changes.Select(change => change.Action + ":" + change.Key));
    }
}
