namespace Boshan.Hub.Activities;

public static class ActivityEndpoints
{
    public static void MapActivities(this WebApplication app)
    {
        static string Bearer(HttpContext ctx) { var h = ctx.Request.Headers.Authorization.ToString(); return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..] : ""; }
        app.MapPost("/v1/activities", async (ActivityCommand command, ActivityService service, HttpContext ctx, IConfiguration config) => {
            if (!config.GetValue<bool>("Activities:Enabled")) throw new HubError("活动正在准备中。", 503);
            var identity = await service.Authorize(Bearer(ctx), ctx.RequestAborted);
            return await service.Command(identity, command, ctx.RequestAborted);
        });
        var group = app.MapGroup("/internal/v1/activities/{instance}");
        group.AddEndpointFilter(async (context, next) => {
            var ctx = context.HttpContext; var instance = ctx.Request.RouteValues["instance"]?.ToString() ?? "";
            ctx.RequestServices.GetRequiredService<ActivityService>().AuthorizeServer(instance, Bearer(ctx));
            return await next(context);
        });
        group.MapGet("/definition", (string instance, ActivityCatalog catalog,HttpContext ctx) => {
            ctx.Response.Headers["X-Activities-Revision"]=catalog.Value.Version.ToString();
            ctx.Response.Headers.CacheControl="no-store";
            return catalog.World(instance);
        });
        group.MapPost("/events", (string instance, ActivityEvent e, ActivityService service, HttpContext ctx) => service.Observe(instance, e, ctx.RequestAborted));
        group.MapGet("/deliveries/{gameUuid}", (string instance, string gameUuid, ActivityService service, HttpContext ctx) => service.Deliveries(instance, gameUuid, ctx.RequestAborted));
        group.MapPost("/deliveries/{gameUuid}/{id:guid}/ack", (string instance, string gameUuid, Guid id, ActivityService service, HttpContext ctx) => service.Acknowledge(instance, gameUuid, id, ctx.RequestAborted));
    }
}
