
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5080");

var app = builder.Build();

app.UseWebSockets();

var rooms = new ConcurrentDictionary<string, Room>();

app.MapGet("/", () => Results.Text("SyncView Server is running"));

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection required");
        return;
    }

    string roomCode = context.Request.Query["room"].ToString().Trim().ToUpperInvariant();

    if (string.IsNullOrWhiteSpace(roomCode) || roomCode.Length > 32)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid room code");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var room = rooms.GetOrAdd(roomCode, _ => new Room());
    room.Clients.TryAdd(socket, 0);

    Console.WriteLine($"Клієнт приєднався до кімнати {roomCode}. Учасників: {room.Clients.Count}");

    try
    {
        var buffer = new byte[8192];

        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    context.RequestAborted);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, result.Count);

                if (message.Length > 64 * 1024)
                    throw new InvalidOperationException("Message too large");

            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            string text = Encoding.UTF8.GetString(message.ToArray());

            foreach (var client in room.Clients.Keys)
            {
                if (client == socket || client.State != WebSocketState.Open)
                    continue;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(text);

                    await client.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        context.RequestAborted);
                }
                catch
                {
                    // Відключений клієнт буде видалений під час завершення з'єднання.
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (WebSocketException)
    {
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"Повідомлення відхилено: {ex.Message}");
    }
    finally
    {
        room.Clients.TryRemove(socket, out _);

        if (room.Clients.IsEmpty)
            rooms.TryRemove(new KeyValuePair<string, Room>(roomCode, room));

        Console.WriteLine($"Клієнт вийшов із кімнати {roomCode}");
    }
});

app.Run();

sealed class Room
{
    public ConcurrentDictionary<WebSocket, byte> Clients { get; } = new();
}
