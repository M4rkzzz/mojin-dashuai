using System.Security.Cryptography;

namespace Boshan.Hub.Activities;

public static class ActivityRules
{
    public static string Day(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
    public static string Week(DateTimeOffset time) => Week(Day(time));
    public static string Week(string day)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd");
        return date.AddDays(-((int)date.DayOfWeek + 6) % 7).ToString("yyyy-MM-dd");
    }
    public static string Month(DateTimeOffset time) => Day(time)[..7];
    public static ActivityStage Stage(ActivityWorld definition, ActivityWorldState state) => definition.Stages.Last(s => s.Requires.Matches(state.Facts));
    public static bool DailyDone(ActivityWorld definition, ActivityWorldState state, string day) => state.Days.TryGetValue(day, out var counts) && definition.Actions.Any(a => counts.GetValueOrDefault(a.Id) >= a.Count);
    public static bool WeeklyDone(ActivityWorld definition, ActivityWorldState state, string week) => state.Weeks.GetValueOrDefault(week) >= 3 || state.Days.Keys.Count(d => Week(d) == week && DailyDone(definition, state, d)) >= 3;
    public static void Observe(ActivityWorld definition, ActivityWorldState state, ActivityEvent e, DateTimeOffset now)
    {
        if (e.EventId == Guid.Empty || e.OccurredAt > now.AddMinutes(5) || e.Count is < 1 or > 4096) throw new HubError("活动事件无效。", 400);
        state.LastSeen = now;
        var allowed = definition.QuestIds.SelectMany(q => new[] { "quest:" + q, "unlocked:" + q }).Concat(definition.TrackedItems.Select(i => "craft:" + i)).Concat(definition.TrackedKills.Select(k => "kill:" + k)).ToHashSet();
        foreach (var fact in e.Facts ?? [])
        {
            if (!allowed.Contains(fact)) throw new HubError("未登记的活动进度。", 400);
            state.Facts.Add(fact);
        }
        if (e.Kind == "snapshot") return; // Existing quest history unlocks pools, never retroactively signs in.
        var key = e.Kind + ":" + e.Key;
        if (!allowed.Contains(key)) throw new HubError("未登记的活动事件。", 400);
        state.Facts.Add(key);
        var day = Day(e.OccurredAt);
        if (!state.Days.TryGetValue(day, out var counts)) state.Days[day] = counts = [];
        var matched = new HashSet<string>();
        foreach (var a in definition.Actions)
        {
            if (a.Kind != e.Kind || (a.Keys.Length != 0 && !a.Keys.Contains(e.Key)) || !a.Requires.Matches(state.Facts)) continue;
            // Quest duplicates are handled by receipt IDs. Repeated original daily completions remain valid.
            counts[a.Id] = Math.Min(a.Count, counts.GetValueOrDefault(a.Id) + e.Count);
            matched.Add(a.Id);
        }
        var week = Week(day); var step = state.Weeks.GetValueOrDefault(week);
        if (step < 3 && definition.WeeklySteps[step].Any(matched.Contains)) state.Weeks[week] = step + 1;
    }
    public static ActivityAward ClaimDaily(ActivityState all, ActivityWorld definition, string day, DateTimeOffset now)
    {
        ValidatePeriod(day, now);
        if (all.DailyClaims.TryGetValue(day, out var claimed)) throw new HubError("这一天已在" + claimed + "领取签到奖励。", 409);
        var state = all.World(definition.Id);
        if (!DailyDone(definition, state, day)) throw new HubError("完成一个当日行动后即可领取。", 409);
        all.DailyClaims[day] = definition.Name; state.Tickets++; state.Medals++;
        var award = new ActivityAward { Tier = "daily", Source = day + " 签到", CreatedAt = now };
        state.Awards.Add(award); return award;
    }
    public static void ClaimWeekly(ActivityWorld definition, ActivityWorldState state, string week, DateTimeOffset now)
    {
        ValidatePeriod(week, now);
        if (Week(week) != week || !WeeklyDone(definition, state, week)) throw new HubError("周活动尚未完成。", 409);
        if (!state.WeeklyClaims.Add(week)) throw new HubError("本周奖励已领取。", 409);
        state.Tickets += 2; state.Medals += 3;
    }
    public static ActivityAward Draw(ActivityWorldState state, DateTimeOffset now, int? deterministicRoll = null)
    {
        if (state.Tickets < 1) throw new HubError("抽奖券不足。", 409);
        var roll = deterministicRoll ?? RandomNumberGenerator.GetInt32(10000);
        if (roll is < 0 or >= 10000) throw new ArgumentOutOfRangeException(nameof(deterministicRoll));
        var tier = state.Misses >= 49 || roll >= 9800 ? "rare" : roll >= 8000 ? "selected" : "ordinary";
        state.Tickets--; state.Misses = tier == "rare" ? 0 : state.Misses + 1;
        var award = new ActivityAward { Tier = tier, Source = "抽奖", CreatedAt = now };
        state.Awards.Add(award); return award;
    }
    public static ActivityReward[] Eligible(ActivityWorld definition, ActivityWorldState state, ActivityAward award) => definition.Rewards.Where(r => r.Tier == award.Tier && r.Requires.Matches(state.Facts)
        && (r.Goal is null || state.GoalBudgets.GetValueOrDefault(r.Goal) + r.BasisPoints <= 3000)).ToArray();
    public static ActivityReward Select(ActivityWorld definition, ActivityWorldState state, ActivityAward award, string rewardId)
    {
        if (award.RewardId is not null) throw new HubError("这份奖励已经选择。", 409);
        var reward = Eligible(definition, state, award).SingleOrDefault(r => r.Id == rewardId) ?? throw new HubError("尚未满足这份奖励的领取条件。", 409);
        if (reward.Goal is not null) state.GoalBudgets[reward.Goal] = state.GoalBudgets.GetValueOrDefault(reward.Goal) + reward.BasisPoints;
        award.RewardId = reward.Id; return reward;
    }
    public static int CosmeticPrice(string id) => id switch { "title" => 8, "frame" => 20, "background" => 40, _ => throw new HubError("未知装饰。", 400) };
    public static void BuyCosmetic(ActivityWorldState state, string id)
    {
        var price = CosmeticPrice(id);
        if (state.Cosmetics.Contains(id)) throw new HubError("已经拥有此装饰。", 409);
        if (state.Medals < price) throw new HubError("纪念章不足。", 409);
        state.Medals -= price; state.Cosmetics.Add(id); Equip(state, id);
    }
    public static void Equip(ActivityWorldState state, string id)
    {
        if (!state.Cosmetics.Contains(id)) throw new HubError("尚未拥有此装饰。", 409);
        switch (id) { case "title": state.EquippedTitle = id; break; case "frame": state.EquippedFrame = id; break; case "background": state.EquippedBackground = id; break; default: throw new HubError("未知装饰。", 400); }
    }
    private static void ValidatePeriod(string day, DateTimeOffset now)
    {
        if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", out _) || StringComparer.Ordinal.Compare(day, Day(now)) > 0) throw new HubError("活动日期无效。", 400);
    }
}
