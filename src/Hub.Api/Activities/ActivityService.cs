using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub.Activities;

public sealed class ActivityCatalog
{
    public ActivityCatalogue Value { get; }
    public ActivityCatalog(IConfiguration config)
    {
        Value = ActivityJson.Read<ActivityCatalogue>(File.ReadAllText(config["Activities:CatalogPath"] ?? Path.Combine(AppContext.BaseDirectory, "activities", "catalog.json")));
        Validate(Value);
    }
    public ActivityWorld World(string id) => Value.Worlds.SingleOrDefault(w => w.Id == id) ?? throw new HubError("未知服务器。", 400);
    public static void Validate(ActivityCatalogue catalogue)
    {
        if (!catalogue.Worlds.Select(w => w.Id).Order().SequenceEqual(new[] { "dc2", "m3e", "mb", "vw" })) throw new InvalidDataException("活动需要四服独立配置。");
        foreach (var w in catalogue.Worlds)
        {
            if (w.Actions.Length != 3 || w.WeeklySteps.Length != 3 || w.WeeklyLabels.Length != 3 || !w.Stages[0].Requires.Matches(new HashSet<string>())) throw new InvalidDataException("活动阶段或目标不完整。");
            if (w.Actions.Select(a => a.Id).Distinct().Count() != 3 || w.Rewards.Select(r => r.Id).Distinct().Count() != w.Rewards.Length) throw new InvalidDataException("活动配置 ID 重复。");
            var facts = w.QuestIds.SelectMany(q => new[] { "quest:" + q, "unlocked:" + q }).Concat(w.TrackedItems.Select(i => "craft:" + i)).Concat(w.TrackedKills.Select(k => "kill:" + k)).ToHashSet();
            foreach (var c in w.Stages.Select(s => s.Requires).Concat(w.Actions.Select(a => a.Requires)).Concat(w.Rewards.Select(r => r.Requires)))
                if (c.All.Concat(c.Any).Concat(c.None).Any(f => !facts.Contains(f))) throw new InvalidDataException("奖励依赖未登记的事实。");
            foreach (var a in w.Actions)
                if (a.Count < 1 || a.Keys.Any(k => !facts.Contains(a.Kind + ":" + k))) throw new InvalidDataException("未登记的活动目标。");
            foreach (var r in w.Rewards)
            {
                if (r.Items.Length is < 1 or > 16 || r.Items.Any(i => i.Count is < 1 or > 64 || i.Meta < 0 || string.IsNullOrWhiteSpace(i.Id) || i.Nbt.Length > 4096)) throw new InvalidDataException("奖励物品无效。");
                if (r.Tier == "rare" && (r.Goal is null || r.BasisPoints is < 1000 or > 2000 || !r.Requires.None.Contains("quest:" + r.Goal))) throw new InvalidDataException("稀有奖励必须指定未完成目标和材料上限。");
                if (r.Items.Any(i => !r.Requires.All.Contains("craft:" + i.Id + "@" + i.Meta))) throw new InvalidDataException("实物奖励必须有对应的实际制作资格。");
            }
        }
    }
}

public sealed class ActivityService(HubDb db, ActivityCatalog catalog, IConfiguration config, JoinRequestLimits limits)
{
    public async Task<JoinIdentity> Authorize(string bearer, CancellationToken ct)
    {
        if (!JoinSecurity.ValidBearer(bearer)) throw new HubError("请重新登录。", 401);
        var hash = Secret.Hash(bearer); var now = DateTimeOffset.UtcNow;
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.AccessHash == hash && s.RevokedAt == null && s.AccessExpiresAt > now, ct);
        JoinIdentity? identity;
        if (session is not null) identity = await db.Set<JoinIdentity>().AsNoTracking().SingleOrDefaultAsync(i => i.HubUserId == session.UserId, ct);
        else
        {
            var grant = await db.Set<JoinGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.AccessHash == hash && g.RevokedAt == null && g.ExpiresAt > now, ct);
            identity = grant is null ? null : await db.Set<JoinIdentity>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == grant.IdentityId, ct);
        }
        if (identity is null || identity.Disabled || (identity.HubUserId is {} user && !await db.Users.AnyAsync(u => u.Id == user && !u.Disabled, ct))) throw new HubError("请重新登录。", 401);
        limits.Take("activities:" + identity.Id, 90);
        return identity;
    }
    public void AuthorizeServer(string instance, string bearer)
    {
        _ = catalog.World(instance);
        var expected = config["Activities:ServerKeys:" + instance];
        if (string.IsNullOrEmpty(expected) || expected.Length < 32 || bearer.Length > 256 || !JoinSecurity.FixedEquals(expected, bearer)) throw new HubError("无效的活动服务凭据。", 403);
    }
    private async Task<ActivityAccount> Lock(Guid identity, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ActivityAccounts\" (\"IdentityId\",\"StateJson\") VALUES ({identity},'{{}}') ON CONFLICT DO NOTHING", ct);
        return await db.Set<ActivityAccount>().FromSqlInterpolated($"SELECT * FROM \"ActivityAccounts\" WHERE \"IdentityId\" = {identity} FOR UPDATE").SingleAsync(ct);
    }
    public async Task<object> Command(JoinIdentity identity, ActivityCommand command, CancellationToken ct)
    {
        var definition = catalog.World(command.Instance); var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await Lock(identity.Id, ct); var all = ActivityJson.Read<ActivityState>(row.StateJson); var state = all.World(command.Instance);
        var mutation = command.Action != "view";
        if (mutation && command.OperationId == Guid.Empty) throw new HubError("操作编号缺失。", 400);
        var fingerprint = ActivityJson.Write(command with { OperationId = Guid.Empty });
        if (mutation && all.Operations.TryGetValue(command.OperationId, out var previous) && previous != fingerprint) throw new HubError("操作编号与请求不符。", 409);
        if (mutation && !all.Operations.ContainsKey(command.OperationId))
        {
            switch (command.Action)
            {
                case "daily": all.OperationAwards[command.OperationId] = ActivityRules.ClaimDaily(all, definition, command.Period ?? ActivityRules.Day(now), now).Id; break;
                case "weekly": ActivityRules.ClaimWeekly(definition, state, command.Period ?? ActivityRules.Week(now), now); break;
                case "draw": all.OperationAwards[command.OperationId] = ActivityRules.Draw(state, now).Id; break;
                case "select":
                {
                    var award = state.Awards.SingleOrDefault(a => a.Id == command.AwardId) ?? throw new HubError("奖励不存在。", 404);
                    var reward = ActivityRules.Select(definition, state, award, command.RewardId ?? "");
                    Queue(identity.Id, definition.Id, award, reward, now); break;
                }
                case "buy": ActivityRules.BuyCosmetic(state, command.Cosmetic ?? ""); break;
                case "equip": ActivityRules.Equip(state, command.Cosmetic ?? ""); break;
                case "showcase":
                {
                    var text = command.Text?.Trim() ?? "";
                    if (text.Length is < 10 or > 700 || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')) throw new HubError("分享内容请填写 10～700 字。", 400);
                    var month = ActivityRules.Month(now);
                    if (await db.Set<ActivityShowcase>().AnyAsync(s => s.IdentityId == identity.Id && s.Instance == definition.Id && s.Month == month, ct)) throw new HubError("本月已提交，正在等待审核或展示。", 409);
                    db.Set<ActivityShowcase>().Add(new() { Id = command.OperationId, IdentityId = identity.Id, Instance = definition.Id, Month = month, Stage = ActivityRules.Stage(definition, state).Name, Text = text, CreatedAt = now }); break;
                }
                default: throw new HubError("未知活动操作。", 400);
            }
            all.Operations[command.OperationId] = fingerprint;
        }
        // Routine supplies become a durable delivery as soon as a verified recipe qualifies.
        foreach (var award in state.Awards.Where(a => a.RewardId is null && a.Tier != "rare"))
        {
            var options = ActivityRules.Eligible(definition, state, award);
            if (options.Length == 0) continue;
            var reward = options[System.Security.Cryptography.RandomNumberGenerator.GetInt32(options.Length)];
            ActivityRules.Select(definition, state, award, reward.Id); Queue(identity.Id, definition.Id, award, reward, now);
        }
        row.StateJson = ActivityJson.Write(all); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await View(identity, definition, all, now, ct, all.OperationAwards.GetValueOrDefault(command.OperationId));
    }
    private void Queue(Guid identity, string instance, ActivityAward award, ActivityReward reward, DateTimeOffset now)
    {
        award.DeliveryId = Guid.NewGuid();
        db.Set<ActivityDelivery>().Add(new() { Id = award.DeliveryId.Value, IdentityId = identity, Instance = instance, AwardId = award.Id, ItemsJson = ActivityJson.Write(reward.Items), CreatedAt = now });
    }
    private async Task<object> View(JoinIdentity identity, ActivityWorld definition, ActivityState all, DateTimeOffset now, CancellationToken ct, string? resultAwardId)
    {
        var state = all.World(definition.Id); var today = ActivityRules.Day(now); var week = ActivityRules.Week(now);
        var delivered = await db.Set<ActivityDelivery>().AsNoTracking().Where(d => d.IdentityId == identity.Id && d.Instance == definition.Id && d.AppliedAt != null).Select(d => d.Id).ToListAsync(ct);
        var showcases = await (from s in db.Set<ActivityShowcase>().AsNoTracking() join i in db.Set<JoinIdentity>() on s.IdentityId equals i.Id where s.Instance == definition.Id && s.Status == "approved" && !i.Disabled orderby s.CreatedAt descending select new { s.Id, s.Text, s.Stage, s.Month, GameName = i.GameName }).Take(8).ToListAsync(ct);
        var myShowcase = await db.Set<ActivityShowcase>().AsNoTracking().Where(s => s.Instance == definition.Id && s.IdentityId == identity.Id && s.Month == ActivityRules.Month(now)).Select(s => s.Status).FirstOrDefaultAsync(ct);
        return new {
            ResultAwardId = resultAwardId,
            Instance = definition.Id, definition.Name, definition.DailyName, definition.WeeklyName, definition.MonthlyName, Today = today, Week = week,
            Stage = ActivityRules.Stage(definition, state).Name, state.LastSeen, state.Tickets, state.Medals, state.Misses, GuaranteeWithin = 50 - state.Misses,
            Actions = definition.Actions.Select(a => new { a.Id, a.Name, a.Description, a.Count, Current = state.Days.GetValueOrDefault(today)?.GetValueOrDefault(a.Id) ?? 0, Eligible = a.Requires.Matches(state.Facts) }),
            DailyReady = ActivityRules.DailyDone(definition, state, today), ClaimedIn = all.DailyClaims.GetValueOrDefault(today),
            PendingDays = state.Days.Keys.Where(d => ActivityRules.DailyDone(definition, state, d) && !all.DailyClaims.ContainsKey(d)).Order().ToArray(),
            WeeklySteps = definition.WeeklyLabels.Select((label, i) => new { Label = label, Done = state.Weeks.GetValueOrDefault(week) > i }),
            WeeklyDays = state.Days.Keys.Count(d => ActivityRules.Week(d) == week && ActivityRules.DailyDone(definition, state, d)),
            WeeklyReady = ActivityRules.WeeklyDone(definition, state, week), WeeklyClaimed = state.WeeklyClaims.Contains(week),
            PendingWeeks = state.Days.Keys.Select(ActivityRules.Week).Concat(state.Weeks.Keys).Distinct().Where(w => !state.WeeklyClaims.Contains(w) && ActivityRules.WeeklyDone(definition, state, w)).Order().ToArray(),
            Awards = state.Awards.Where(a => a.DeliveryId is null || !delivered.Contains(a.DeliveryId.Value)).Concat(state.Awards.Where(a => a.DeliveryId is {} d && delivered.Contains(d)).OrderByDescending(a => a.CreatedAt).Take(80)).OrderByDescending(a => a.CreatedAt).Select(a => new { a.Id, a.Tier, a.Source, a.CreatedAt, a.RewardId,
                Items = definition.Rewards.FirstOrDefault(r => r.Id == a.RewardId)?.Items ?? [],
                Name = definition.Rewards.FirstOrDefault(r => r.Id == a.RewardId)?.Name, Status = a.DeliveryId is {} d ? delivered.Contains(d) ? "delivered" : "queued" : "pending",
                Choices = a.RewardId is null ? ActivityRules.Eligible(definition, state, a).Select(r => new { r.Id, r.Name, r.Purpose, r.Items, r.BasisPoints }) : [] }),
            Pool = definition.Rewards.Select(r => new { r.Id, r.Name, r.Tier, r.Purpose, r.Items, r.BasisPoints,
                Eligible = ActivityRules.Eligible(definition, state, new() { Tier = r.Tier }).Contains(r), Eligibility = RewardEligibility(r, state) }),
            state.Cosmetics, state.EquippedTitle, state.EquippedFrame, state.EquippedBackground, Showcases = showcases, MyShowcase = myShowcase
        };
    }
    private static string RewardEligibility(ActivityReward reward, ActivityWorldState state)
    {
        if (reward.Goal is {} goal && state.Facts.Contains("quest:" + goal)) return "这个建设目标已完成。";
        if (reward.Goal is {} budget && state.GoalBudgets.GetValueOrDefault(budget) + reward.BasisPoints > 3000) return "本目标的累计材料补足额度不足，最多补足 30%。";
        if (reward.Requires.All.Any(f => f.StartsWith("craft:") && !state.Facts.Contains(f))) return "先在游戏中自行制作这份材料，取得制作资格。";
        return reward.Requires.Matches(state.Facts) ? "已满足领取条件。" : "原有任务前置完成、当前目标开放后可领取。";
    }
    public async Task<object> Observe(string instance, ActivityEvent e, CancellationToken ct)
    {
        var definition = catalog.World(instance);
        if (!Guid.TryParse(e.GameUuid, out var uuid)) throw new HubError("游戏身份无效。", 400);
        var canonical = uuid.ToString();
        var identity = await db.Set<JoinIdentity>().AsNoTracking().SingleOrDefaultAsync(i => i.GameUuid == canonical && !i.Disabled, ct) ?? throw new HubError("活动身份尚未登记。", 404);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await Lock(identity.Id, ct);
        var receipt = await db.Set<ActivityReceipt>().AsNoTracking().SingleOrDefaultAsync(r => r.Instance == instance && r.EventId == e.EventId, ct);
        if (receipt is not null) { if (receipt.IdentityId != identity.Id) throw new HubError("事件编号冲突。", 409); return new { accepted = true }; }
        var all = ActivityJson.Read<ActivityState>(row.StateJson);
        ActivityRules.Observe(definition, all.World(instance), e, DateTimeOffset.UtcNow);
        row.StateJson = ActivityJson.Write(all);
        db.Set<ActivityReceipt>().Add(new() { Instance = instance, EventId = e.EventId, IdentityId = identity.Id, ReceivedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { accepted = true };
    }
    public async Task<object> Deliveries(string instance, string gameUuid, CancellationToken ct)
    {
        if (!Guid.TryParse(gameUuid, out var uuid)) throw new HubError("游戏身份无效。", 400);
        var canonical = uuid.ToString();
        var identity = await db.Set<JoinIdentity>().AsNoTracking().SingleOrDefaultAsync(i => i.GameUuid == canonical && !i.Disabled, ct);
        if (identity is null) return Array.Empty<object>();
        var items = await db.Set<ActivityDelivery>().AsNoTracking().Where(d => d.Instance == instance && d.IdentityId == identity.Id && d.AppliedAt == null).OrderBy(d => d.CreatedAt).Take(8).ToListAsync(ct);
        return items.Select(d => new { d.Id, Items = ActivityJson.Read<ActivityItem[]>(d.ItemsJson) });
    }
    public async Task<object> Acknowledge(string instance, string gameUuid, Guid delivery, CancellationToken ct)
    {
        var identity = await db.Set<JoinIdentity>().AsNoTracking().SingleOrDefaultAsync(i => i.GameUuid == gameUuid && !i.Disabled, ct) ?? throw new HubError("身份无效。", 404);
        var d = await db.Set<ActivityDelivery>().SingleOrDefaultAsync(d => d.Id == delivery && d.Instance == instance && d.IdentityId == identity.Id, ct) ?? throw new HubError("投递不存在。", 404);
        d.AppliedAt ??= DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return new { accepted = true };
    }
}
