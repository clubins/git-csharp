using Scion.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScionServer();

var app = builder.Build();

app.UseScionServer();
app.MapScionEndpoints();

app.Run();

// Public and partial so WebApplicationFactory-based tests can reference the host type.
public partial class Program
{
}
