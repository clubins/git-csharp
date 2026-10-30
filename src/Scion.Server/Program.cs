using Scion.Server;
using Scion.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScionServer();
builder.Services.AddScionStorage();

var app = builder.Build();

app.UseScionServer();
app.MapScionEndpoints();

app.Run();

// Public and partial so WebApplicationFactory-based tests can reference the host type.
public partial class Program
{
}
