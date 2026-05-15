using System.Collections.Concurrent;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Services;

public sealed class GoogleOAuthRevokedException : Exception
{
    public GoogleOAuthRevokedException(int linkshellId, Exception inner)
        : base($"Google refused the stored refresh token for linkshell {linkshellId}.", inner)
    {
        LinkshellId = linkshellId;
    }

    public int LinkshellId { get; }
}

public sealed class GoogleSheetsSyncService
{
    internal const string DataProtectionPurpose = "Linkshell.GoogleOAuth.RefreshToken";
    private const string ApplicationName = "LSManager";

    private readonly GoogleSheetsOptions _options;
    private readonly IDataProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GoogleSheetsSyncService> _logger;

    private readonly Lazy<GoogleAuthorizationCodeFlow?> _flow;
    private readonly ConcurrentDictionary<int, SheetsService> _serviceCache = new();

    public GoogleSheetsSyncService(
        IOptions<GoogleSheetsOptions> options,
        IDataProtectionProvider protectionProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<GoogleSheetsSyncService> logger)
    {
        _options = options.Value;
        _protector = protectionProvider.CreateProtector(DataProtectionPurpose);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _flow = new Lazy<GoogleAuthorizationCodeFlow?>(BuildFlow, isThreadSafe: true);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId) &&
        !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task<bool> IsConnectedAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => l.GoogleOAuthRefreshTokenEnc != null)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public void InvalidateCache(int linkshellId)
    {
        if (_serviceCache.TryRemove(linkshellId, out var existing))
        {
            existing.Dispose();
        }
    }

    public string ProtectRefreshToken(string refreshToken)
    {
        return _protector.Protect(refreshToken);
    }

    public string UnprotectRefreshToken(string ciphertext)
    {
        return _protector.Unprotect(ciphertext);
    }

    public Task<IList<IList<object>>?> ReadAsync(int linkshellId, string spreadsheetId, string range, CancellationToken cancellationToken)
        => ReadAsync(linkshellId, spreadsheetId, range, unformatted: false, cancellationToken);

    public async Task<IList<IList<object>>?> ReadAsync(int linkshellId, string spreadsheetId, string range, bool unformatted, CancellationToken cancellationToken)
    {
        var service = await GetServiceAsync(linkshellId, cancellationToken)
            ?? throw new InvalidOperationException($"Linkshell {linkshellId} is not connected to Google Sheets.");
        try
        {
            var request = service.Spreadsheets.Values.Get(spreadsheetId, range);
            if (unformatted)
            {
                request.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
            }
            var response = await request.ExecuteAsync(cancellationToken);
            return response.Values;
        }
        catch (TokenResponseException ex) when (IsInvalidGrant(ex))
        {
            InvalidateCache(linkshellId);
            throw new GoogleOAuthRevokedException(linkshellId, ex);
        }
    }

    public async Task WriteAsync(int linkshellId, string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken)
    {
        var service = await GetServiceAsync(linkshellId, cancellationToken)
            ?? throw new InvalidOperationException($"Linkshell {linkshellId} is not connected to Google Sheets.");
        try
        {
            var body = new ValueRange { Values = values };
            var request = service.Spreadsheets.Values.Update(body, spreadsheetId, range);
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await request.ExecuteAsync(cancellationToken);
        }
        catch (TokenResponseException ex) when (IsInvalidGrant(ex))
        {
            InvalidateCache(linkshellId);
            throw new GoogleOAuthRevokedException(linkshellId, ex);
        }
    }

    // Appends rows to the end of a sheet without overwriting existing cells.
    // Uses INSERT_ROWS so the formulas / data above the insertion point keep
    // their row indices stable. USER_ENTERED so date strings, formula refs,
    // etc. are parsed the same way a typed cell would be.
    public async Task<AppendValuesResponse> AppendAsync(int linkshellId, string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken)
    {
        var service = await GetServiceAsync(linkshellId, cancellationToken)
            ?? throw new InvalidOperationException($"Linkshell {linkshellId} is not connected to Google Sheets.");
        try
        {
            var body = new ValueRange { Values = values };
            var request = service.Spreadsheets.Values.Append(body, spreadsheetId, range);
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            request.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
            return await request.ExecuteAsync(cancellationToken);
        }
        catch (TokenResponseException ex) when (IsInvalidGrant(ex))
        {
            InvalidateCache(linkshellId);
            throw new GoogleOAuthRevokedException(linkshellId, ex);
        }
    }

    public async Task FormatRowAsync(
        int linkshellId,
        string spreadsheetId,
        string tabName,
        int rowNumber,
        float red,
        float green,
        float blue,
        CancellationToken cancellationToken)
    {
        if (rowNumber <= 0)
        {
            return;
        }

        var service = await GetServiceAsync(linkshellId, cancellationToken)
            ?? throw new InvalidOperationException($"Linkshell {linkshellId} is not connected to Google Sheets.");
        try
        {
            var sheetId = await GetSheetIdAsync(service, spreadsheetId, tabName, cancellationToken);
            if (!sheetId.HasValue)
            {
                _logger.LogWarning("Could not find sheet tab {TabName} in spreadsheet {SpreadsheetId}.", tabName, spreadsheetId);
                return;
            }

            var request = new BatchUpdateSpreadsheetRequest
            {
                Requests = new List<Request>
                {
                    new()
                    {
                        RepeatCell = new RepeatCellRequest
                        {
                            Range = new GridRange
                            {
                                SheetId = sheetId.Value,
                                StartRowIndex = rowNumber - 1,
                                EndRowIndex = rowNumber,
                                StartColumnIndex = 0,
                                EndColumnIndex = 11,
                            },
                            Cell = new CellData
                            {
                                UserEnteredFormat = new CellFormat
                                {
                                    BackgroundColor = new Color
                                    {
                                        Red = red,
                                        Green = green,
                                        Blue = blue,
                                    },
                                },
                            },
                            Fields = "userEnteredFormat.backgroundColor",
                        },
                    },
                },
            };

            await service.Spreadsheets.BatchUpdate(request, spreadsheetId).ExecuteAsync(cancellationToken);
        }
        catch (TokenResponseException ex) when (IsInvalidGrant(ex))
        {
            InvalidateCache(linkshellId);
            throw new GoogleOAuthRevokedException(linkshellId, ex);
        }
    }

    private static async Task<int?> GetSheetIdAsync(
        SheetsService service,
        string spreadsheetId,
        string tabName,
        CancellationToken cancellationToken)
    {
        var request = service.Spreadsheets.Get(spreadsheetId);
        request.Fields = "sheets.properties(sheetId,title)";
        var spreadsheet = await request.ExecuteAsync(cancellationToken);
        return spreadsheet.Sheets?
            .Select(s => s.Properties)
            .FirstOrDefault(p => string.Equals(p.Title, tabName, StringComparison.OrdinalIgnoreCase))
            ?.SheetId;
    }

    public async Task ClearAsync(int linkshellId, string spreadsheetId, string range, CancellationToken cancellationToken)
    {
        var service = await GetServiceAsync(linkshellId, cancellationToken)
            ?? throw new InvalidOperationException($"Linkshell {linkshellId} is not connected to Google Sheets.");
        try
        {
            var request = service.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, range);
            await request.ExecuteAsync(cancellationToken);
        }
        catch (TokenResponseException ex) when (IsInvalidGrant(ex))
        {
            InvalidateCache(linkshellId);
            throw new GoogleOAuthRevokedException(linkshellId, ex);
        }
    }

    private async Task<SheetsService?> GetServiceAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (_serviceCache.TryGetValue(linkshellId, out var cached))
        {
            return cached;
        }

        var flow = _flow.Value;
        if (flow is null) return null;

        string? ciphertext;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ciphertext = await db.Linkshells
                .AsNoTracking()
                .Where(l => l.Id == linkshellId)
                .Select(l => l.GoogleOAuthRefreshTokenEnc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(ciphertext)) return null;

        string refreshToken;
        try
        {
            refreshToken = _protector.Unprotect(ciphertext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unprotect Google OAuth refresh token for linkshell {LinkshellId}; clearing.", linkshellId);
            await ClearTokenInDbAsync(linkshellId, cancellationToken);
            return null;
        }

        var token = new TokenResponse { RefreshToken = refreshToken };
        var userId = $"linkshell-{linkshellId}";
        var credential = new UserCredential(flow, userId, token);

        var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });

        if (_serviceCache.TryAdd(linkshellId, service))
        {
            return service;
        }
        service.Dispose();
        return _serviceCache[linkshellId];
    }

    private async Task ClearTokenInDbAsync(int linkshellId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var linkshell = await db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
            if (linkshell is null) return;
            linkshell.GoogleOAuthRefreshTokenEnc = null;
            linkshell.GoogleOAuthUserEmail = null;
            linkshell.GoogleOAuthConnectedAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear corrupt OAuth token for linkshell {LinkshellId}.", linkshellId);
        }
    }

    private GoogleAuthorizationCodeFlow? BuildFlow()
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Google Sheets OAuth not configured (GoogleSheets:ClientId / GoogleSheets:ClientSecret).");
            return null;
        }

        return new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _options.ClientId,
                ClientSecret = _options.ClientSecret,
            },
            Scopes = new[] { SheetsService.Scope.Spreadsheets },
            DataStore = new NullDataStore(),
        });
    }

    private static bool IsInvalidGrant(TokenResponseException ex)
    {
        return string.Equals(ex.Error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NullDataStore : IDataStore
    {
        public Task ClearAsync() => Task.CompletedTask;
        public Task DeleteAsync<T>(string key) => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key) => Task.FromResult<T>(default!);
        public Task StoreAsync<T>(string key, T value) => Task.CompletedTask;
    }
}
