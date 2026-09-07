using System.Text.Json;

namespace Boshan.Hub.Activities;

public sealed record ActivityCondition(string[] All, string[] Any, string[] None)
{
    public bool Matches(ISet<string> facts) => All.All(facts.Contains) && (Any.Length == 0 || Any.Any(facts.Contains)) && !None.Any(facts.Contains);
    public static ActivityCondition Empty { get; } = new([], [], []);
}
public sealed record ActivityStage(string Id, string Name, ActivityCondition Requires);
public sealed record ActivityAction(string Id, string Name, string Description, string Kind, string[] Keys, int Count, ActivityCondition Requires);
public sealed record ActivityItem(string Id, int Meta, int Count, string Nbt = "{}");
public sealed record ActivityReward(string Id, string Name, string Tier, string Purpose, ActivityItem[] Items, ActivityCondition Requires, string? Goal = null, int BasisPoints = 0, bool CompleteSet = false, bool Retired = false);
public sealed record ActivityWorld(string Id, string Name, string DailyName, string WeeklyName, string MonthlyName, ActivityStage[] Stages, ActivityAction[] Actions,
    string[][] WeeklySteps, string[] WeeklyLabels, string[] QuestIds, string[] TrackedItems, string[] TrackedKills, ActivityReward[] Rewards, string[]? TrackedControllers = null);
public sealed record ActivityCatalogue(int Version, ActivityWorld[] Worlds);
public sealed record ActivityGoals(ActivityAction[] Actions,string[][] WeeklySteps,string[] WeeklyLabels);
public sealed record ActivityEvent(Guid EventId, string GameUuid, DateTimeOffset OccurredAt, string Kind, string Key, int Count = 1, string[]? Facts = null);
public sealed record ActivityCommand(string Instance, string Action = "view", Guid OperationId = default, string? Period = null, string? AwardId = null, string? RewardId = null, string? Cosmetic = null, string? Text = null);

// One row lock covers the shared daily quota and every per-world wallet for an identity.
public sealed class ActivityAccount
{
    public Guid IdentityId { get; set; }
    public string StateJson { get; set; } = "{}";
}
public sealed class ActivityReceipt
{
    public string Instance { get; set; } = "";
    public Guid EventId { get; set; }
    public Guid IdentityId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
public sealed class ActivityDelivery
{
    public Guid Id { get; set; }
    public Guid IdentityId { get; set; }
    public string Instance { get; set; } = "";
    public string AwardId { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}
public sealed class ActivityShowcase
{
    public Guid Id { get; set; }
    public Guid IdentityId { get; set; }
    public string Instance { get; set; } = "";
    public string Month { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class ActivityState
{
    public Dictionary<string, ActivityWorldState> Worlds { get; set; } = [];
    public Dictionary<string, string> DailyClaims { get; set; } = [];
    public Dictionary<Guid, string> Operations { get; set; } = [];
    public Dictionary<Guid, string> OperationAwards { get; set; } = [];
    public ActivityWorldState World(string id) { if (!Worlds.TryGetValue(id, out var w)) Worlds[id] = w = new(); return w; }
}
public sealed class ActivityWorldState
{
    public HashSet<string> Facts { get; set; } = [];
    public Dictionary<string, Dictionary<string, int>> Days { get; set; } = [];
    public Dictionary<string, int> Weeks { get; set; } = [];
    public Dictionary<string, ActivityGoals> GoalPeriods { get; set; } = [];
    public HashSet<string> WeeklyClaims { get; set; } = [];
    public int Tickets { get; set; }
    public int Medals { get; set; }
    public int Misses { get; set; }
    public Dictionary<string, int> GoalBudgets { get; set; } = [];
    public HashSet<string> ClaimedSets { get; set; } = [];
    public List<ActivityAward> Awards { get; set; } = [];
    public HashSet<string> Cosmetics { get; set; } = [];
    public string? EquippedTitle { get; set; }
    public string? EquippedFrame { get; set; }
    public string? EquippedBackground { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
}
public sealed class ActivityAward
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tier { get; set; } = "ordinary";
    public string Source { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? RewardId { get; set; }
    public Guid? DeliveryId { get; set; }
}
public static class ActivityJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("活动数据无效。");
}
