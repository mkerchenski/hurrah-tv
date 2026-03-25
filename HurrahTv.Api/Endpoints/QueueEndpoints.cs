using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class QueueEndpoints
{
    public static void MapQueueEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/queue");

        group.MapGet("", async (DbService db) =>
        {
            var items = await db.GetQueueAsync();
            return Results.Ok(items);
        });

        group.MapPost("", async (QueueItem item, DbService db) =>
        {
            var added = await db.AddToQueueAsync(item);
            return added != null ? Results.Created($"/api/queue/{added.Id}", added) : Results.Conflict("Already in queue");
        });

        group.MapDelete("/{id:int}", async (int id, DbService db) =>
        {
            bool removed = await db.RemoveFromQueueAsync(id);
            return removed ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{id:int}/status", async (int id, QueueStatusUpdate update, DbService db) =>
        {
            bool updated = await db.UpdateStatusAsync(id, update.Status);
            return updated ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{id:int}/position", async (int id, PositionUpdate update, DbService db) =>
        {
            bool updated = await db.ReorderAsync(id, update.Position);
            return updated ? Results.Ok() : Results.NotFound();
        });
    }

    public record QueueStatusUpdate(QueueStatus Status);
    public record PositionUpdate(int Position);
}
