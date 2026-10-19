using Ombra.Server;

var builder = WebApplication.CreateBuilder(args);
// install.sh writes /etc/ombra/server.env (Ombra__*); environment variables override appsettings.json.
ServerApp.ConfigureServices(builder);
var app = builder.Build();
ServerApp.Map(app);
await app.RunAsync();

public partial class Program;
