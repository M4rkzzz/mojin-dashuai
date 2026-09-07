using Boshan.Hub;
using Boshan.Hub.Activities;
using Xunit;

namespace Boshan.Tests;

public sealed class ActivityTests
{
    private static ActivityCatalogue Catalogue()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "activities/catalog.json"))) root = root.Parent;
        return ActivityJson.Read<ActivityCatalogue>(File.ReadAllText(Path.Combine(root!.FullName, "activities/catalog.json")));
    }
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 15, 59, 0, TimeSpan.Zero);
    private static ActivityEvent Event(string kind, string key, int count = 1, DateTimeOffset? at = null, string[]? facts = null) => new(Guid.NewGuid(), Guid.NewGuid().ToString(), at ?? Now, kind, key, count, facts);
    [Fact] public void RealCatalogueHasAllWorldsAndEveryItemIsGatedByItsOwnRecipe()
    {
        var c = Catalogue(); ActivityCatalog.Validate(c);
        Assert.All(c.Worlds, w => { Assert.Equal(3, w.Actions.Length); Assert.Contains(w.Rewards, r => r.Tier == "rare"); Assert.DoesNotContain(w.Rewards, r => r.Requires.Matches(new HashSet<string>())); });
        var m3e = c.Worlds.Single(w => w.Id == "m3e");
        Assert.DoesNotContain(m3e.Rewards.SelectMany(r => r.Items), i => i.Id.Contains("GoldenPrism") || i.Id.Contains("ItemKeyRoll"));
    }
    [Fact] public void SharedDailyQuotaCannotBeClaimedAgainOnAnotherWorldAndMissedDaysSurvive()
    {
        var c = Catalogue(); var state = new ActivityState(); var today = ActivityRules.Day(Now);
        foreach (var w in c.Worlds) ActivityRules.Observe(w, state.World(w.Id), Event("craft", "minecraft:torch@0", 8), Now);
        ActivityRules.ClaimDaily(state, c.Worlds[0], today, Now);
        Assert.Throws<HubError>(() => ActivityRules.ClaimDaily(state, c.Worlds[1], today, Now));
        var old = Now.AddDays(-40); var w0 = c.Worlds[0];
        ActivityRules.Observe(w0, state.World(w0.Id), Event("craft", "minecraft:torch@0", 8, old), Now);
        var restored = ActivityJson.Read<ActivityState>(ActivityJson.Write(state));
        ActivityRules.ClaimDaily(restored, w0, ActivityRules.Day(old), Now);
        Assert.Equal(2, restored.World(w0.Id).Tickets);
    }
    [Fact] public void SnapshotUnlocksButDoesNotCreditHistoricalOrCheckboxTasks()
    {
        var w = Catalogue().Worlds.Single(w => w.Id == "mb"); var s = new ActivityWorldState();
        ActivityRules.Observe(w, s, Event("snapshot", "", facts: ["quest:" + w.QuestIds[0]]), Now);
        Assert.False(ActivityRules.DailyDone(w, s, ActivityRules.Day(Now)));
        Assert.Throws<HubError>(() => ActivityRules.Observe(w, s, Event("quest", "0"), Now)); // 强大的知识：手动勾选。
        Assert.Throws<HubError>(() => ActivityRules.Observe(w, s, Event("pickup", "minecraft:torch@0", 999), Now));
    }
    [Fact] public void WeeklyAlternativeCountsDifferentChinaCalendarDaysAndDoesNotResetOnMissedDay()
    {
        var w = Catalogue().Worlds[0];var s = new ActivityWorldState();
        foreach (var offset in new[] { -5, -3, 0 }) ActivityRules.Observe(w, s, Event("craft", "minecraft:torch@0", 8, Now.AddDays(offset)), Now);
        Assert.True(ActivityRules.WeeklyDone(w, s, ActivityRules.Week(Now)));
        ActivityRules.ClaimWeekly(w, s, ActivityRules.Week(Now), Now);Assert.Equal(2, s.Tickets);Assert.Equal(3, s.Medals);
        Assert.Throws<HubError>(() => ActivityRules.ClaimWeekly(w, s, ActivityRules.Week(Now), Now));
        Assert.NotEqual(ActivityRules.Day(Now), ActivityRules.Day(Now.AddMinutes(1)));
        Assert.NotEqual(ActivityRules.Week(Now), ActivityRules.Week(Now.AddMinutes(1)));
    }
    [Fact] public void LotteryUsesExactBaseThresholdsAndFiftiethDrawGuarantee()
    {
        var s = new ActivityWorldState { Tickets = 55 };
        Assert.Equal("ordinary", ActivityRules.Draw(s, Now, 7999).Tier);
        Assert.Equal("selected", ActivityRules.Draw(s, Now, 8000).Tier);
        Assert.Equal("selected", ActivityRules.Draw(s, Now, 9799).Tier);
        Assert.Equal("rare", ActivityRules.Draw(s, Now, 9800).Tier);
        for (var i = 0; i < 49; i++) Assert.Equal("ordinary", ActivityRules.Draw(s, Now, 0).Tier);
        var afterRestart = ActivityJson.Read<ActivityWorldState>(ActivityJson.Write(s));
        Assert.Equal("rare", ActivityRules.Draw(afterRestart, Now, 0).Tier); Assert.Equal(0, afterRestart.Misses);
        Assert.Throws<HubError>(() => ActivityRules.Draw(new(), Now));
    }
    [Fact] public void WholeStructureRequiresItsOriginalGateAndPartsAndCanOnlyBeClaimedOnce()
    {
        foreach (var w in Catalogue().Worlds)
        {
            var r = w.Rewards.First(r => r.CompleteSet); var s = new ActivityWorldState(); var award = new ActivityAward { Tier = "rare" };
            Assert.Empty(ActivityRules.Eligible(w, s, award));
            s.Facts.UnionWith(r.Requires.All); if (r.Requires.Any.Length > 0) s.Facts.Add(r.Requires.Any[0]);
            Assert.Contains(r, ActivityRules.Eligible(w, s, award));
            ActivityRules.Select(w, s, award, r.Id);
            Assert.Throws<HubError>(() => ActivityRules.Select(w, s, award, r.Id));
            s = ActivityJson.Read<ActivityWorldState>(ActivityJson.Write(s));
            Assert.Throws<HubError>(() => ActivityRules.Select(w, s, new() { Tier = "rare" }, r.Id));
            Assert.Equal(10000,s.GoalBudgets[r.Goal!]);
            var completed = new ActivityWorldState();completed.Facts.UnionWith(r.Requires.All);completed.Facts.Add("quest:"+r.Goal);
            Assert.Contains(r, ActivityRules.Eligible(w, completed, new() { Tier = "rare" }));
            completed.Facts.Remove(r.Requires.All.First(f=>f.StartsWith("craft:")));
            Assert.DoesNotContain(r, ActivityRules.Eligible(w, completed, new() { Tier = "rare" }));
        }
    }
    [Fact] public void PreviousPartialRewardsAndDeliveriesArePreservedButNoLongerDrawn()
    {
        var w=Catalogue().Worlds.Single(w=>w.Id=="vw");var old=w.Rewards.Single(r=>r.Id=="tower-wall");
        Assert.True(old.Retired);Assert.Equal(10,old.Items.Single().Count);
        var set=w.Rewards.Single(r=>r.Id=="v2-set-tower");var state=new ActivityWorldState();
        state.Facts.UnionWith(old.Requires.All.Concat(set.Requires.All));state.Facts.Add("quest:"+set.Goal);
        state.GoalBudgets[set.Goal!]=3000;
        Assert.DoesNotContain(old,ActivityRules.Eligible(w,state,new(){Tier="rare"}));
        Assert.Contains(set,ActivityRules.Eligible(w,state,new(){Tier="rare"}));
        ActivityRules.Select(w,state,new(){Tier="rare"},set.Id);
        Assert.DoesNotContain(set,ActivityRules.Eligible(w,state,new(){Tier="rare"}));
    }
    [Fact] public void FullTowerDeliveryContainsAllEightyOneBlocksAcrossLegalStacks()
    {
        var tower=Catalogue().Worlds.Single(w=>w.Id=="vw").Rewards.Single(r=>r.Id=="v2-set-tower");
        var stacks=ActivityService.DeliveryItems(tower.Items);
        Assert.Equal(81,stacks.Sum(i=>i.Count));Assert.All(stacks,i=>Assert.InRange(i.Count,1,64));
        Assert.Equal(new[]{64,7},stacks.Where(i=>i.Meta==18102).Select(i=>i.Count));
        Assert.Equal(9,stacks.Single(i=>i.Meta==18101).Count);Assert.Equal(1,stacks.Single(i=>i.Meta==17101).Count);
        foreach(var w in Catalogue().Worlds)foreach(var r in w.Rewards.Where(r=>!r.Retired))Assert.Equal(r.Items.Sum(i=>i.Count),ActivityService.DeliveryItems(r.Items).Sum(i=>i.Count));
    }
    [Fact] public void OwningAControllerUnlocksOnlyThatCoreAndNeverCountsAsProductionOrADaily()
    {
        var w=Catalogue().Worlds.Single(w=>w.Id=="vw");var s=new ActivityWorldState();var key="gregtech:gt.multitileentity@17101";
        ActivityRules.Observe(w,s,Event("snapshot","",facts:["owned:"+key]),Now);
        Assert.Contains("owned:"+key,s.Facts);Assert.DoesNotContain("craft:"+key,s.Facts);
        Assert.False(ActivityRules.DailyDone(w,s,ActivityRules.Day(Now)));
        Assert.Throws<HubError>(()=>ActivityRules.Observe(w,s,Event("owned",key,99),Now));
        Assert.Throws<HubError>(()=>ActivityRules.Observe(w,s,Event("snapshot","",facts:["owned:minecraft:diamond@0"]),Now));
        Assert.Empty(ActivityRules.Eligible(w,s,new(){Tier="rare"}));
    }
    [Fact] public void PendingRareAndWalletArePreservedWhenNoRewardQualifiesAndCosmeticCannotBeBoughtTwice()
    {
        var w=Catalogue().Worlds[0];var s=new ActivityWorldState { Tickets=1, Medals=68 };
        var a=ActivityRules.Draw(s,Now,9999);Assert.Empty(ActivityRules.Eligible(w,s,a));Assert.Null(a.RewardId);
        foreach(var id in new[]{"title","frame","background"})ActivityRules.BuyCosmetic(s,id);
        Assert.Equal(0,s.Medals);Assert.Equal(3,s.Cosmetics.Count);Assert.Throws<HubError>(()=>ActivityRules.BuyCosmetic(s,"title"));
        var restored=ActivityJson.Read<ActivityWorldState>(ActivityJson.Write(s));Assert.Null(restored.Awards.Single().RewardId);Assert.Equal("title",restored.EquippedTitle);
    }
    [Fact] public void LiveConfigRetiresRemovedPrizesButCannotRewriteAlreadyAwardedItems()
    {
        var before=Catalogue();var w=before.Worlds[0];var removed=w.Rewards.First(r=>!r.Retired);
        var next=before with{Version=before.Version+1,Worlds=before.Worlds.Select(p=>p.Id==w.Id?p with{Rewards=p.Rewards.Where(r=>r.Id!=removed.Id).ToArray()}:p).ToArray()};
        var merged=ActivityCatalog.Merge(before,next);
        var retained=merged.Worlds[0].Rewards.Single(r=>r.Id==removed.Id);
        Assert.True(retained.Retired);Assert.Equal(ActivityJson.Write(removed.Items),ActivityJson.Write(retained.Items));
        var changed=before with{Worlds=before.Worlds.Select(p=>p.Id==w.Id?p with{Rewards=p.Rewards.Select(r=>r.Id==removed.Id?r with{Items=[r.Items[0] with{Count=r.Items[0].Count+1}]}:r).ToArray()}:p).ToArray()};
        Assert.Throws<InvalidDataException>(()=>ActivityCatalog.Merge(before,changed));
    }
    [Fact] public void StartedWeeklyGoalsAndUnclaimedDaysSurviveLiveChangesNewWeekUsesNewGoals()
    {
        var w=Catalogue().Worlds[0];var s=new ActivityWorldState();
        ActivityRules.Observe(w,s,Event("craft","minecraft:torch@0",8),Now);
        var changed=w with{Actions=w.Actions.Select(a=>a with{Id="next-"+a.Id,Count=20}).ToArray(),WeeklySteps=w.WeeklySteps.Select(step=>step.Select(id=>"next-"+id).ToArray()).ToArray()};
        Assert.True(ActivityRules.DailyDone(changed,s,ActivityRules.Day(Now)));
        Assert.Equal(w.Actions,ActivityRules.EffectiveGoals(changed,s,ActivityRules.Week(Now)).Actions);
        var nextWeek=Now.AddDays(7);ActivityRules.Observe(changed,s,Event("craft","minecraft:torch@0",8,nextWeek),nextWeek);
        Assert.False(ActivityRules.DailyDone(changed,s,ActivityRules.Day(nextWeek)));
        var restored=ActivityJson.Read<ActivityWorldState>(ActivityJson.Write(s));
        Assert.True(ActivityRules.DailyDone(changed,restored,ActivityRules.Day(Now)));
        ActivityRules.Observe(changed,restored,Event("craft","minecraft:torch@0",12,nextWeek),nextWeek);
        Assert.True(ActivityRules.DailyDone(changed,restored,ActivityRules.Day(nextWeek)));
    }
}
