using AutoUpdaterServer.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Server:Url"] ?? "http://127.0.0.1:5000");
builder.Services.AddSingleton<ConnectionManager>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

app.MapGet("/", (ConnectionManager connections) => Results.Ok(new
{
    service = "AutoUpdaterServer",
    status = "running",
    managers = connections.ManagerCount,
    devices = connections.DeviceCount
}));

app.Map("/ws/manage", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connections = context.RequestServices.GetRequiredService<ConnectionManager>();
    await connections.HandleManagerAsync(socket, context.RequestAborted);
});

app.Map("/ws/device", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var deviceId = context.Request.Query["deviceId"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("deviceId is required");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connections = context.RequestServices.GetRequiredService<ConnectionManager>();
    await connections.HandleDeviceAsync(deviceId, socket, context.RequestAborted);
});

app.Run();
