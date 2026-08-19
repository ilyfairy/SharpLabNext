using SharpLabNext.Worker.Roslyn;

if (RoslynBuildChild.IsInvocation(args))
{
    await RoslynBuildChild.RunAsync(WebApplication.CreateBuilder([]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var app = RoslynWorkerHost.Build(builder);
app.Run();

public partial class Program;
