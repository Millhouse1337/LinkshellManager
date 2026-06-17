namespace LinkshellManagerDiscordApp.Services;

// Startup task: makes the Discord Activity's "Launch" entry-point command app-handled
// so Discord stops auto-posting the public "Game Invitation / Join" card to the channel
// when someone launches the Activity (our interactions endpoint handles the launch
// quietly instead). Best-effort and idempotent — fired off without blocking startup;
// DiscordBotClient swallows + logs any failure.
public sealed class DiscordEntryPointConfigurator : IHostedService
{
    // DiscordBotClient is scoped, so resolve it through a scope kept alive for the
    // duration of the (awaited) call.
    private readonly IServiceScopeFactory _scopeFactory;

    public DiscordEntryPointConfigurator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget so a slow/unreachable Discord never delays app startup.
        _ = RunAsync();
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var bot = scope.ServiceProvider.GetRequiredService<DiscordBotClient>();
            await bot.EnsureEntryPointAppHandledAsync(CancellationToken.None);
        }
        catch
        {
            // EnsureEntryPointAppHandledAsync already logs its own failures; this is a
            // last-resort guard so the fire-and-forget never throws unobserved.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
