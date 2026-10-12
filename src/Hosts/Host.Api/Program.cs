// Phase 0 placeholder: no feature code. Real composition root arrives in Phase 1.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok()).AllowAnonymous();
app.Run();
